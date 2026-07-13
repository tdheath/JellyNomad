using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace JellyNomad;

public class NomadChannel : IChannel
{
    private readonly ILogger<NomadChannel> _logger;
    private readonly IServerApplicationHost _applicationHost;

    public NomadChannel(
        ILogger<NomadChannel> logger,
        IServerApplicationHost applicationHost)
    {
        _logger = logger;
        _applicationHost = applicationHost;
    }

    public string Name => "Nomad";
    public string Description => "Browse your Nomad channels.";

    public string DataVersion
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null) return "0";

            var key = string.Join("|", config.Channels.Select(c =>
                $"{c.Id}:{c.Name}:{HashImage(c.ChannelImage)}:{c.SelectedSeriesIds.Length}"));
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        }
    }

    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => true;

    public IEnumerable<ImageType> GetSupportedChannelImages()
        => [ImageType.Primary];

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        var assembly = typeof(NomadChannel).Assembly;
        var stream = assembly.GetManifestResourceStream("JellyNomad.Images.channel-icon.png");

        if (stream is null)
        {
            _logger.LogWarning(
                "[NomadChannel] channel-icon.png not found. Available resources: {Resources}",
                string.Join(", ", assembly.GetManifestResourceNames()));
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Format = ImageFormat.Png,
            Stream = stream
        });
    }

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType>
        {
            ChannelMediaContentType.Episode,
            ChannelMediaContentType.Movie
        },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        SupportsSortOrderToggle = false,
        DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.Name }
    };

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return Task.FromResult(new ChannelItemResult());

        if (!string.IsNullOrEmpty(query.FolderId))
            return Task.FromResult(new ChannelItemResult());

        var baseUrl = $"http://localhost:{_applicationHost.HttpPort}";

        var channelItems = config.Channels.Select(ch =>
        {
            var itemId = ToItemId(ch.Id, ch.ChannelImage);
            return new ChannelItemInfo
            {
                Id = itemId,
                Name = ch.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                ImageUrl = string.IsNullOrEmpty(ch.ChannelImage)
                    ? null
                    : $"{baseUrl}/JellyNomad/ChannelImage/{ch.Id}?v={HashImage(ch.ChannelImage)}",

                // Point to the infinite streaming endpoint; 
                MediaSources = new List<MediaSourceInfo>
                {
                    new()
                    {
                        Id = itemId,
                        Name = ch.Name,
                        Path = $"{baseUrl}/JellyNomad/Channel/{ch.Id}/stream",
                        Protocol = MediaProtocol.Http,
                        IsInfiniteStream = true,
                        IsRemote = false,
                        Container = "ts",
                        SupportsTranscoding = true,
                        SupportsDirectStream = true,
                        MediaStreams = new List<MediaStream>
                        {
                            new()
                            {
                                Type = MediaStreamType.Video,
                                Codec = "h264",
                                Index = 0,
                                IsDefault = true,
                                IsInterlaced = false
                            },
                            new()
                            {
                                Type = MediaStreamType.Audio,
                                Codec = "aac",
                                Channels = 2,
                                SampleRate = 48000,
                                Index = 1,
                                IsDefault = true
                            }
                        }
                    }
                }
            };
        }).ToList();

        return Task.FromResult(new ChannelItemResult
        {
            Items = channelItems,
            TotalRecordCount = channelItems.Count
        });
    }

    private static string ToItemId(string channelId, string? channelImage = null)
    {
        var key = $"JellyNomad.Channel:{channelId}:{HashImage(channelImage)}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToString("N");
    }

    private static string HashImage(string? img)
    {
        if (string.IsNullOrEmpty(img)) return "0";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(img)))[..8];
    }
}
