namespace Tec.Driver.Abi;

/// <summary>
/// 最小的热流实现。放在 ABI 里，是为了让驱动不必为了发一条采样就引用 System.Reactive
/// ——第三方驱动的依赖越少，AssemblyLoadContext 里打架的机会越少（§3.5）。
/// </summary>
public sealed class Broadcast<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = new();
    private readonly object _gate = new();
    private bool _completed;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        lock (_gate)
        {
            if (_completed)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }
            _observers.Add(observer);
        }
        return new Subscription(this, observer);
    }

    public void Push(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            if (_completed || _observers.Count == 0) return;
            snapshot = _observers.ToArray();
        }
        foreach (var o in snapshot)
        {
            try { o.OnNext(value); }
            catch { /* 一个订阅者出错不许拖垮采集 */ }
        }
    }

    public void Complete()
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            snapshot = _observers.ToArray();
            _observers.Clear();
        }
        foreach (var o in snapshot)
        {
            try { o.OnCompleted(); } catch { }
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate) _observers.Remove(observer);
    }

    private sealed class Subscription : IDisposable
    {
        private Broadcast<T>? _owner;
        private readonly IObserver<T> _observer;
        public Subscription(Broadcast<T> owner, IObserver<T> observer)
        {
            _owner = owner;
            _observer = observer;
        }
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            o?.Remove(_observer);
        }
    }
}

public sealed class Disposable : IDisposable
{
    public static readonly IDisposable Empty = new Disposable(null);
    private Action? _action;
    private Disposable(Action? action) => _action = action;
    public static IDisposable Create(Action action) => new Disposable(action);
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

/// <summary>把 lambda 当订阅者用。</summary>
public sealed class DelegateObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    private readonly Action<Exception>? _onError;
    private readonly Action? _onCompleted;

    public DelegateObserver(Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
    {
        _onNext = onNext;
        _onError = onError;
        _onCompleted = onCompleted;
    }

    public void OnCompleted() => _onCompleted?.Invoke();
    public void OnError(Exception error) => _onError?.Invoke(error);
    public void OnNext(T value) => _onNext(value);
}

public static class AbiObservableExtensions
{
    /// <summary>刻意不叫 ObservableExtensions：将来谁引了 System.Reactive 也不会撞名。</summary>
    public static IDisposable SubscribeTo<T>(this IObservable<T> source, Action<T> onNext)
        => source.Subscribe(new DelegateObserver<T>(onNext));
}
