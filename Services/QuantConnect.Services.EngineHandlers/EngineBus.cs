using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// Process-wide bus, object store and job context shared by the bus handlers, which LEAN's <c>Composer</c>
/// creates by type name with parameterless constructors. Production resolves RabbitMQ and MinIO from config on
/// first use; tests call <see cref="Use"/> with an in-memory bus before the engine starts.
/// </summary>
public static class EngineBus
{
    private static readonly object Lock = new();
    private static IMessageBus? _bus;
    private static bool _ownsBus;
    private static IArtifactStore? _store;

    /// <summary>The running job attempt; set by <see cref="BusJobQueueHandler.NextJob"/>.</summary>
    public static BusJobContext? Job { get; set; }

    /// <summary>Overrides the bus and store; the bus is closed by <see cref="Close"/> only when <paramref name="ownsBus"/>.</summary>
    public static void Use(IMessageBus bus, IArtifactStore? store = null, bool ownsBus = false)
    {
        lock (Lock)
        {
            _bus = bus;
            _ownsBus = ownsBus;
            _store = store;
        }
    }

    public static IMessageBus Bus
    {
        get
        {
            lock (Lock)
            {
                if (_bus == null)
                {
                    _bus = new RabbitMqMessageBus(new RabbitMqOptions
                    {
                        Uri = new Uri(BusSettings.Require(BusSettings.RabbitMqUri)),
                        ClientName = $"lean-engine-{Job?.JobId:D}",
                    });
                    _ownsBus = true;
                }
                return _bus;
            }
        }
    }

    public static IArtifactStore Store
    {
        get
        {
            lock (Lock)
            {
                return _store ??= ArtifactStore.FromConfig();
            }
        }
    }

    /// <summary>Closes a bus this class opened (the broker connection); an injected bus is left alone.</summary>
    public static void Close(TimeSpan timeout)
    {
        IMessageBus? owned;
        lock (Lock)
        {
            owned = _ownsBus ? _bus : null;
            if (_ownsBus)
            {
                _bus = null;
                _ownsBus = false;
            }
        }
        switch (owned)
        {
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().Wait(timeout);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>Forgets the bus, store and job (tests).</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _bus = null;
            _ownsBus = false;
            _store = null;
            Job = null;
        }
    }
}
