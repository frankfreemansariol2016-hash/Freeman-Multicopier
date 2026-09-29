using System.Buffers;

namespace FreemanMultiCopier.Core.Engine;

/// <summary>
/// Bloque de datos en vuelo, compartido por N escritores. El buffer vuelve al pool
/// cuando el ULTIMO escritor lo suelta, asi la RAM queda acotada sin importar cuantos
/// dispositivos copien a la vez.
/// </summary>
public sealed class CopyBlock
{
    private int _refCount;

    private CopyBlock() { }

    public byte[] Buffer { get; private set; } = Array.Empty<byte>();

    /// <summary>Bytes validos en <see cref="Buffer"/>.</summary>
    public int Length { get; private init; }

    public int Capacity => Buffer.Length;

    /// <summary>Escritores que aun deben consumirlo.</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    /// Envuelve un buffer ya alquilado. La propiedad del buffer pasa al bloque: el
    /// llamante no debe devolverlo, lo devuelve <see cref="Release"/>.
    /// </summary>
    public static CopyBlock FromRented(byte[] rented, int length, int memberCount) => new()
    {
        Buffer = rented,
        Length = length,
        _refCount = Math.Max(1, memberCount),
    };

    /// <summary>
    /// Pool propio, y no el compartido de .NET. Ojo con el motivo, porque se ha escrito
    /// mal: el compartido NO tiene un tope de 1 MB. En .NET 10 son 27 cubos que llegan
    /// hasta 1 GB, y un bloque de 2 MB cae en el cubo 17, que es exactamente de 2 MB, o
    /// sea que SI se recicla. El tope de 1 MB era de las primeras versiones de .NET Core
    /// y se quedo escrito en el codigo como si fuera todavia verdad.
    ///
    /// Lo que si se puede decir a favor de este pool: una sola lista plana de 64 buffers
    /// por cubo, sin cache por hilo ni particiones por nucleo, con un techo conocido de
    /// 8 MB y sin depender de las variables de entorno del compartido. Lo que NO se puede
    /// decir es que sea mas rapido: la subida de 155 a 1300 MB/s es del doble buffer de
    /// MultiCopyEngine, y la aportacion de este pool no se midio aislada nunca.
    /// </summary>
    /// 64 y no 32 porque una sesion puede tener 24 bloques de recorrido mas uno por
    /// destino enganchado, y con 32 el pool se quedaba corto y acababa reservando fuera.
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(8 * 1024 * 1024, 64);

    /// <summary>Alquila un buffer del tamano pedido. Se devuelve con <see cref="ReturnBuffer"/> si falla la lectura.</summary>
    public static byte[] RentBuffer(int size) => Pool.Rent(size);

    public static void ReturnBuffer(byte[] buffer) => Pool.Return(buffer);

    /// <summary>Devuelve true si este fue el ultimo y el buffer vuelve al pool.</summary>
    public bool Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return false;
        Pool.Return(Buffer);
        Buffer = Array.Empty<byte>();
        return true;
    }
}

/// <summary>
/// Presupuesto de RAM del motor. El canal de cada sesion es COMPARTIDO por sus N
/// escritores, asi que la memoria de una sesion es capacidad x tamano de bloque y no
/// crece con el numero de dispositivos. Lo que evita que la RAM se dispare es el techo
/// de sesiones simultaneas: cada sesion corresponde a un archivo distinto en vuelo.
/// </summary>
public sealed class BufferBudget
{
    private readonly int _blockSize;
    private readonly int _maxBlocksPerSession;
    private readonly int _maxConcurrentSessions;

    /// <param name="blockSize">Bytes de cada bloque en vuelo.</param>
    /// <param name="sessionBufferBytes">
    /// RAM de UNA sesion. El total del motor es esto por el numero de sesiones a la vez,
    /// porque cada sesion es un archivo distinto en vuelo y las de un mismo archivo
    /// comparten canal y buffers.
    /// </param>
    /// <param name="maxConcurrentSessions">Archivos distintos que se pueden copiar a la vez.</param>
    public BufferBudget(int blockSize, long sessionBufferBytes, int maxConcurrentSessions)
    {
        _blockSize = Math.Clamp(blockSize, 64 * 1024, 4 * 1024 * 1024);
        long affordable = Math.Max(4, sessionBufferBytes / _blockSize);
        _maxBlocksPerSession = (int)Math.Clamp(affordable, 4, 48);
        _maxConcurrentSessions = Math.Max(1, maxConcurrentSessions);
    }

    /// <summary>
    /// El presupuesto que usa el programa. Vive aqui y no en el sitio donde se construye
    /// el servicio, para que el banco de pruebas mida exactamente lo mismo que la app.
    /// </summary>
    /// <remarks>
    /// Los tres numeros salen de medir, no de suposiciones, con el banco de pruebas.
    /// <list type="bullet">
    /// <item>Bloque de 2 MB. Medido en copia en solitario con 600 MB, el 27-09-2026:
    /// 1 MB dio 1415 MB/s, 2 MB dio 1467, 4 MB dio 1446 y 8 MB dio 863. Entre 1, 2 y 4
    /// la diferencia es el ruido del disco, pero 8 MB se cae a la mitad y no se debe
    /// usar: ademas un bloque tan grande hace que la cola de destino se llene antes,
    /// que es justo lo que ahoga a las copias en solitario. Se queda en 2 MB porque al
    /// mismo rendimiento se lleva la mitad de RAM que con 4 MB.</item>
    /// <item>48 MB por sesion, o sea 24 bloques de recorrido. Esto SI se nota: es lo
    /// que separa un origen rapido de un destino lento. Con 32 MB de recorrido el pump
    /// se quedaba esperando y el conjunto bajaba a 89 MB/s; con 96 MB, 125.</item>
    /// <item>6 archivos distintos a la vez. Con 3 se quedaban la mitad de los destinos
    /// mirando la cola sin copiar nada.</item>
    /// </list>
    /// Conviene recordar al leer estas cifras que el techo de escritura de C: esta
    /// entre 85 y 97 MB/s segun el dia, asi que un conjunto de 4 destinos a C: rondando
    /// los 100 MB/s ya esta al limite del disco, no del programa.
    /// En el peor caso son 6 sesiones de 48 MB mas un bloque por destino enganchado,
    /// unos 310 MB. En el caso normal, mucho menos: las sesiones de un mismo archivo
    /// comparten buffers, y solo hay un destino por sesion mientras copia.
    /// </remarks>
    public static BufferBudget CreateDefault() =>
        new BufferBudget(blockSize: 2 * 1024 * 1024, sessionBufferBytes: 48L * 1024 * 1024,
                       maxConcurrentSessions: 6);

    public int BlockSize => _blockSize;

    public int MaxBlocksPerSession => _maxBlocksPerSession;

    public int MaxConcurrentSessions => _maxConcurrentSessions;

    /// <summary>RAM maxima teorica que puede retener el motor.</summary>
    public long WorstCaseBytes => (long)_maxBlocksPerSession * _blockSize * _maxConcurrentSessions;

    /// <summary>Usado por la UI para el indicador de RAM.</summary>
    public static long PhysicalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}

/// <summary>
/// Limitador de escritura por dispositivo, en bytes por segundo. Es lo que permite que
/// "dar prioridad" a un dispositivo signifique de verdad mas ancho de banda para el.
/// </summary>
public interface IWriteThrottle
{
    /// <summary>Espera lo que haga falta antes de escribir <paramref name="bytes"/>.</summary>
    Task WaitAsync(int bytes, CancellationToken ct);
}

/// <summary>Sin limitacion: el dispositivo escribe a maxima velocidad.</summary>
public sealed class NoThrottle : IWriteThrottle
{
    public static readonly NoThrottle Instance = new();
    public Task WaitAsync(int bytes, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Limitador de token bucket, suavizado con una ventana movil.</summary>
public sealed class TokenBucketThrottle : IWriteThrottle
{
    private readonly long _rate;       // bytes por segundo admitidos
    private readonly long _burst;      // margen instantaneo, para no trocear la escritura
    private readonly object _gate = new();
    private long _tokens;
    private long _lastTicks;

    public TokenBucketThrottle(long bytesPerSecond)
    {
        _rate = Math.Max(0, bytesPerSecond);
        _burst = Math.Max(_rate, 64 * 1024);
        _tokens = _burst;
        _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public Task WaitAsync(int bytes, CancellationToken ct)
    {
        if (_rate <= 0) return Task.CompletedTask;

        long waitMs;
        lock (_gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsed = (double)(now - _lastTicks) / System.Diagnostics.Stopwatch.Frequency;
            _lastTicks = now;

            // Se rellena lo negociado con el paso del tiempo y se descuenta lo que se va a escribir.
            _tokens = Math.Min(_burst, _tokens + (long)(elapsed * _rate));
            _tokens -= bytes;

            if (_tokens >= 0) return Task.CompletedTask;

            double secondsToRecover = -_tokens / (double)_rate;
            waitMs = (long)(secondsToRecover * 1000.0);
        }

        return Task.Delay((int)Math.Clamp(waitMs, 1L, 60_000L), ct);
    }
}
