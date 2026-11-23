using Rosella.Drm;

namespace Rosella.Compositor;

/// <summary>Demultiplexes page-flip events from the card shared by the render loops.</summary>
internal sealed class DrmFlipQueue : IDisposable
{
    private readonly DrmDevice _device;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _pending = new();
    private readonly Thread _reader;
    private Exception? _failure;

    internal DrmFlipQueue(DrmDevice device)
    {
        _device = device;
        _reader = new Thread(Read) { IsBackground = true, Name = "rosella-drm-events" };
        _reader.Start();
    }

    internal Task Submit(uint crtc, uint framebuffer, ulong cookie)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            if (_failure is not null)
            {
                throw new InvalidOperationException("DRM event reader stopped", _failure);
            }

            _pending.Add(cookie, completed);
        }

        try
        {
            _device.PageFlip(crtc, framebuffer, cookie);
            return completed.Task;
        }
        catch
        {
            Forget(cookie);
            throw;
        }
    }

    internal void Forget(ulong cookie)
    {
        lock (_pending)
        {
            _pending.Remove(cookie);
        }
    }

    private void Read()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (var notification in _device.ReadEvents(_stop.Token))
                {
                    if (notification.Type == DrmConstants.EventFlipComplete)
                    {
                        lock (_pending)
                        {
                            if (_pending.Remove(notification.UserData, out var completed))
                            {
                                completed.TrySetResult();
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_pending)
            {
                _failure = exception;
                foreach (var completed in _pending.Values)
                {
                    completed.TrySetException(exception);
                }

                _pending.Clear();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _reader.Join();
        _stop.Dispose();
    }
}
