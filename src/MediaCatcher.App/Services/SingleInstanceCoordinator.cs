using MediaCatcher.Contracts;

namespace MediaCatcher.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    public SingleInstanceCoordinator(string mutexName)
    {
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
    }

    public bool IsFirstInstance => _ownsMutex;

    public static async Task<bool> TryActivateExistingAsync(string pipeName)
    {
        var client = new NamedPipeIpcClient(pipeName);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var response = await client
                .SendAsync(IpcRequest.ForAction(IpcActions.Activate, "launcher"), TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            if (response.Ok)
            {
                return true;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread.
            }
        }

        _mutex.Dispose();
    }
}
