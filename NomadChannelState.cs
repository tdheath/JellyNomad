using System.Collections.Concurrent;

namespace JellyNomad;

public class NomadChannelState
{
    private const int FILELOCKBUFFER = 3000;
    private const int OLDCHANNELBUFFER = 10000;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _episodeCts = new();
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, TaskCompletionSource Tcs)> _streams = new();

    public void Register(string channelId, CancellationTokenSource cts)
        => _episodeCts[channelId] = cts;

    public void Deregister(string channelId, CancellationTokenSource cts)
        => _episodeCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(channelId, cts));

    public bool Skip(string channelId)
    {
        if (_episodeCts.TryGetValue(channelId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public async Task<(CancellationTokenSource Cts, TaskCompletionSource Tcs)> RegisterStreamAsync(string channelId, CancellationToken httpToken)
    {
        if (_streams.TryRemove(channelId, out var old))
        {
            old.Cts.Cancel();
            // Give the old StreamChannel's finally block a little time to complete.
            await Task.WhenAny(old.Tcs.Task, Task.Delay(TimeSpan.FromMilliseconds(OLDCHANNELBUFFER), httpToken));
            httpToken.ThrowIfCancellationRequested();

            //give an extra second for FFmpeg to release the file lock
            await Task.Delay(FILELOCKBUFFER, httpToken);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(httpToken);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _streams[channelId] = (cts, tcs);
        return (cts, tcs);
    }

    public void CompleteStream(string channelId, CancellationTokenSource cts, TaskCompletionSource tcs)
    {
        _streams.TryRemove(channelId, out _);
        tcs.TrySetResult();
        cts.Dispose();
    }
}
