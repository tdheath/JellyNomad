using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyNomad;

[ApiController]
[Route("JellyNomad")]
[AllowAnonymous]
public partial class NomadStreamController : ControllerBase
{
    [GeneratedRegex(@"""duration""\s*:\s*""([0-9.]+)""")]
    private static partial Regex DurationRegex();
    private const int RETRYLIMIT = 5;
    private const int BUFFERMILLISECONDS = 5000;
    private readonly ILogger<NomadStreamController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly NomadChannelState _state;

    public NomadStreamController(
        ILogger<NomadStreamController> logger,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        NomadChannelState state)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _state = state;
    }

    [HttpGet("Channel/{channelId}/stream")]
    public async Task StreamChannel(string channelId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var channel = config?.Channels.FirstOrDefault(c => c.Id == channelId);
        if (channel is null || channel.SelectedSeriesIds.Length == 0)
        {
            Response.StatusCode = 404;
            return;
        }

        var playHistory = new Queue<(string seriesName, DateTime playedAt)>();
        var breakCandidates = new Dictionary<string, DateTime>();

        // Cancel any prior stream for this channel and wait briefly
        var (streamCts, streamTcs) = await _state.RegisterStreamAsync(channelId, cancellationToken);
        var streamToken = streamCts.Token;

        Response.ContentType = "video/mp2t";
        Response.StatusCode = 200;
        await Response.StartAsync(streamToken);


        List<MediaBrowser.Controller.Entities.TV.Episode>? episodes = await GetAvailableEpisodes(channel, streamToken);
        if (episodes is null || episodes.Count == 0)
        {
            _logger.LogError("[NomadChannel] No episodes found for channel {channelId}", channelId);
            _state.CompleteStream(channelId, streamCts, streamTcs);
            return;
        }

        // Accumulate media time across episodes so that ts offset always increasing timestamps. 
        // Otherwise next episodes reset to 0 and throw a 500 error.
        double tsOffsetSeconds = 0.0;
        int episodesSinceBreak = 0;

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 64 * 1024 * 1024,
            resumeWriterThreshold: 32 * 1024 * 1024,
            useSynchronizationContext: false));
        var pipeWriterStream = pipe.Writer.AsStream();
        var readerTask = pipe.Reader.AsStream().CopyToAsync(Response.Body, CancellationToken.None);

        if (channel.AllowCommercials)
        {
            AddCommercials(config, breakCandidates);
        }

        if (channel.AllowTrailers)
        {
            AddTrailers(channel, breakCandidates);
        }

        try
        {
            while (!streamToken.IsCancellationRequested)
            {
                var episodeToPlay = GetNextEpisode(channel, playHistory, episodes);
                _logger.LogInformation("[NomadChannel] {Channel}: now playing '{Episode}'", channel.Name, episodeToPlay.Name);

                // Per-episode CTS linked to the stream token. Canceling it
                // moves to the next episode without closing the stream.
                // TODO: Implement skip functionality
                using var episodeCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
                _state.Register(channelId, episodeCts);

                // Transcode to h264/aac so that:
                //   (a) codec parameters (resolution etc.) are always present in the MPEG-TS
                //       headers — mpeg4/divx files don't embed this, which makes Jellyfin's
                //       FFmpeg report "unspecified size" and fail to start encoding;
                //   (b) audio sample-rate is normalised (48 kHz) so Jellyfin's AAC encoder
                //       never sees format changes mid-stream.
                var args = $"-i \"{episodeToPlay.Path}\" " +
                        $"-c:v libx264 -preset ultrafast -crf 18 " +
                        $"-c:a aac -ar 48000 -ac 2 " +
                        $"-output_ts_offset {tsOffsetSeconds:F3} " +
                        $"-f mpegts -loglevel error pipe:1";

                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                try
                {
                    process.Start();
                    _ = process.StandardError.ReadToEndAsync(streamToken);
                    await process.StandardOutput.BaseStream
                        .CopyToAsync(pipeWriterStream, episodeCts.Token);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (OperationCanceledException) when (!streamToken.IsCancellationRequested)
                {
                    if (!process.HasExited)
                        try { process.Kill(entireProcessTree: true); } catch { }
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        try { process.Kill(entireProcessTree: true); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[NomadChannel] Error streaming '{Episode}', skipping", episodeToPlay.Name);
                    if (!process.HasExited)
                        try { process.Kill(entireProcessTree: true); } catch { }
                    await Task.Delay(500, streamToken);
                }
                finally
                {
                    _state.Deregister(channelId, episodeCts);
                    if (episodeToPlay.RunTimeTicks.HasValue)
                        tsOffsetSeconds += episodeToPlay.RunTimeTicks.Value / 10_000_000.0;
                    playHistory.Enqueue((episodeToPlay.SeriesName ?? string.Empty, DateTime.UtcNow));
                    episodesSinceBreak++;
                }

                if ((channel.AllowCommercials || channel.AllowTrailers)
                    && episodesSinceBreak >= Math.Max(1, channel.EpisodesPerBreak)
                    && !streamToken.IsCancellationRequested)
                {
                    episodesSinceBreak = 0;

                    var availableBreaks = breakCandidates
                        .Where(kvp => kvp.Value < DateTime.Now.AddDays(-1))
                        .Select(kvp => kvp.Key)
                        .OrderBy(_ => Random.Shared.Next())
                        .Take(Math.Max(1, channel.BreakItemCount))
                        .ToList();

                    foreach (var breakPath in availableBreaks)
                    {
                        if (streamToken.IsCancellationRequested) break;

                        _logger.LogInformation(
                            "[NomadChannel] {Channel}: playing break '{File}'",
                            channel.Name, Path.GetFileName(breakPath));

                        var duration = await GetFileDurationAsync(breakPath, streamToken);
                        var bArgs = $"-i \"{breakPath}\" " +
                                    $"-c:v libx264 -preset ultrafast -crf 18 " +
                                    $"-c:a aac -ar 48000 -ac 2 " +
                                    $"-output_ts_offset {tsOffsetSeconds:F3} " +
                                    $"-f mpegts -loglevel error pipe:1";

                        using var bp = new Process();
                        bp.StartInfo = new ProcessStartInfo
                        {
                            FileName = _mediaEncoder.EncoderPath,
                            Arguments = bArgs,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var streamError = false;
                        try
                        {
                            bp.Start();
                            _ = bp.StandardError.ReadToEndAsync(streamToken);
                            await bp.StandardOutput.BaseStream
                                .CopyToAsync(pipeWriterStream, streamToken);
                            await bp.WaitForExitAsync(CancellationToken.None);
                            breakCandidates[breakPath] = DateTime.Now;
                        }
                        catch (OperationCanceledException)
                        {
                            if (!bp.HasExited)
                                try { bp.Kill(entireProcessTree: true); } catch { }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[NomadChannel] Error streaming break '{File}', skipping",
                                breakPath);
                            if (!bp.HasExited)
                                try { bp.Kill(entireProcessTree: true); } catch { }
                            streamError = true;
                        }

                        if (!streamError)
                            tsOffsetSeconds += duration;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await pipe.Writer.CompleteAsync();
            try { await readerTask; } catch { }
            _state.CompleteStream(channelId, streamCts, streamTcs);
        }
    }

    private static MediaBrowser.Controller.Entities.TV.Episode GetNextEpisode(Configuration.ChannelConfiguration channel, 
                                                                              Queue<(string seriesName, DateTime playedAt)> playHistory, 
                                                                              List<MediaBrowser.Controller.Entities.TV.Episode> episodes)
    {
        var now = DateTime.UtcNow;

        //Clear history but keep the last successiveEpisodes to check against that
        while (playHistory.Count > channel.SuccessiveEpisodesAllowed
            && playHistory.Peek().playedAt < now.AddHours(-channel.HoursBetweenRepeat))
            playHistory.Dequeue();

        List<string> recentSeries = channel.HoursBetweenRepeat > 0
            ? [.. playHistory.Where(h => h.playedAt >= now.AddHours(-channel.HoursBetweenRepeat))
                                    .Select(h => h.seriesName)]
            : [];

        string? repeatedSeries = null;
        if (channel.SuccessiveEpisodesAllowed > 0 && playHistory.Count >= channel.SuccessiveEpisodesAllowed)
        {
            var successiveEps = playHistory.TakeLast(channel.SuccessiveEpisodesAllowed);
            var potentialRepeat = successiveEps.First().seriesName;
            if (successiveEps.All(h => h.seriesName == potentialRepeat))
            {
                repeatedSeries = potentialRepeat;
            }
        }

        var candidates = episodes
                        .Where(ep => !recentSeries.Contains(ep.SeriesName) && ep.SeriesName != repeatedSeries)
                        .ToList();

        //If nothing is available, first drop the series repeat limit
        if (candidates.Count == 0 && repeatedSeries is not null)
            candidates = episodes
                        .Where(ep => !recentSeries.Contains(ep.SeriesName))
                        .ToList();

        // If still nothing available, just return the whole list
        if (candidates.Count == 0)
        {
            candidates = episodes;
        }

        var episodeToPlay = candidates[Random.Shared.Next(candidates.Count)];
        return episodeToPlay;
    }

    private void AddTrailers(Configuration.ChannelConfiguration channel, Dictionary<string, DateTime> breakCandidates)
    {
        try
        {
            var trailers = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Trailer],
                IsVirtualItem = false
            });
            trailers
            .Where(t => IsWithinRating(t.OfficialRating, channel.MaxTrailerRating))
            .Select(t => t.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList()
            .ForEach(com => breakCandidates.Add(com, new DateTime()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NomadChannel] Failed to query trailers for channel.");
        }
    }

    private static void AddCommercials(Configuration.PluginConfiguration? config, Dictionary<string, DateTime> breakCandidates)
    {
        var commercialsPath = config?.CommercialsPath;

        if (!string.IsNullOrEmpty(commercialsPath) && Directory.Exists(commercialsPath))
        {
            //TODO: Better way to do this?
            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".m4v", ".ts", ".m2ts" };
            Directory.GetFiles(commercialsPath)
            .Where(f => videoExts.Contains(Path.GetExtension(f)))
            .Distinct()
            .ToList()
            .ForEach(com => breakCandidates.Add(com, new DateTime()));
        }
    }

    private async Task<List<MediaBrowser.Controller.Entities.TV.Episode>?> GetAvailableEpisodes(Configuration.ChannelConfiguration channel, CancellationToken streamToken)
    {
        IReadOnlyList<BaseItem> episodes;
        var channelSeriesIds = channel.SelectedSeriesIds
                    .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToArray();
        for(int i = 0; i<RETRYLIMIT; i++)
        {
            try
            {
                episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = channelSeriesIds,
                    IsVirtualItem = false
                });

                if(episodes.Count > 0)
                {
                    return episodes.OfType<MediaBrowser.Controller.Entities.TV.Episode>().ToList();
                    
                }
                
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NomadChannel] Failed to query library for channel");
            }
            await Task.Delay(BUFFERMILLISECONDS, streamToken);
        }
        return null;
    }

    private async Task<double> GetFileDurationAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new Process();
            probe.StartInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.ProbePath,
                Arguments = $"-v quiet -print_format json -show_format \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probe.Start();
            var json = await probe.StandardOutput.ReadToEndAsync(cancellationToken);
            await probe.WaitForExitAsync(CancellationToken.None);
            var match = DurationRegex().Match(json);
            if (match.Success && double.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var dur))
                return dur;
        }
        catch { }
        return 0;
    }

    // TODO: This was AIs suggestion for a skip,
   /* [HttpGet("Channel/{channelName}/skip")]
    public IActionResult SkipEpisode(string channelName)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
        if (channel is null) return NotFound();
        if (_state.Skip(channel.Id)) return Ok();
        return NotFound();
    }*/

    private static bool IsWithinRating(string? itemRating, string? maxRating)
    {
        if (string.IsNullOrEmpty(maxRating)) return true;

        // Items with an unknown rating should be treated as "R"
        if (string.IsNullOrEmpty(itemRating))
        {
            itemRating = "R";    
        }
        

        // US MPAA rank order; anything not in this map is treated as unrated (allowed).
        Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase)
        {
            { "G",     0 },
            { "PG",    1 },
            { "PG-13", 2 },
            { "R",     3 },
            { "NC-17", 4 }
        };

        if (!rank.TryGetValue(itemRating, out var itemRank)) return true;
        if (!rank.TryGetValue(maxRating,  out var maxRank))  return true;
        return itemRank <= maxRank;
    }

}
