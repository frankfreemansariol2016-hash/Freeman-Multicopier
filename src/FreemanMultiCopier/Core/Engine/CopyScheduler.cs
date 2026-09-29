using System.Collections.Concurrent;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.Core.Engine;

/// <summary>Criterio con el que se reparte el ancho de banda entre los dispositivos que copian a la vez.</summary>
public enum PriorityStrategy
{
    /// <summary>Atiende primero al que menos le falta: los clientes salen uno a uno.</summary>
    LeastRemaining = 0,

    /// <summary>Reparte segun el porcentaje de avance: todos progresan parejo.</summary>
    ByPercentage = 1,

    /// <summary>Da mas a los dispositivos que ya van mas rapido: aprovecha los rapidos.</summary>
    AverageSpeed = 2,
}

/// <summary>
/// Reparte el ancho de banda entre los dispositivos que estan copiando.
///
/// No fijamos la velocidad de un dispositivo de forma absoluta: fijamos la PROPORCION que
/// le toca respecto a lo que los demas estan consumiendo de verdad. Asi el conjunto va tan
/// rapido como el hardware permite, y lo unico que se decide es como se reparte.
///
/// Importante: nunca asignamos menos del ancho de banda real que el conjunto ya demuestra
/// poder alcanzar. Limitar por debajo de lo posible solo haria la copia mas lenta sin
/// repartir mejor nada.
/// </summary>
public sealed class CopyScheduler
{
    private readonly ConcurrentDictionary<string, long> _measuredSpeed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastKnownTotal = new(StringComparer.OrdinalIgnoreCase);

    public PriorityStrategy Strategy { get; set; } = PriorityStrategy.LeastRemaining;

    /// <summary>Techo de RAM que el motor puede retener entre todas las sesiones.</summary>
    public long MaxBufferBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Suma de las velocidades medidas. Es el techo con el que no tiene sentido bajar.</summary>
    public long ObservedThroughput { get; private set; }

    public void ReportSpeed(string deviceId, long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return;
        _measuredSpeed.AddOrUpdate(deviceId, bytesPerSecond, (_, old) =>
            (long)(old * 0.6 + bytesPerSecond * 0.4));
    }

    public void Forget(string deviceId)
    {
        _measuredSpeed.TryRemove(deviceId, out _);
        _lastKnownTotal.TryRemove(deviceId, out _);
    }

    public void Reset() => _measuredSpeed.Clear();

    /// <summary>
    /// Calcula el limite de cada dispositivo activo y se lo aplica al motor.
    /// Los que no estan trabajando, o los pausados, se quedan sin limite.
    /// </summary>
    public void Rebalance(IReadOnlyList<CopyJob> jobs, MultiCopyEngine engine)
    {
        var active = jobs.Where(j => j.State == JobState.Working).ToArray();
        if (active.Length == 0) return;

        if (active.Length == 1)
        {
            // Solo uno trabajando: que vaya a maxima. Limitarlo no aporta nada.
            engine.SetThrottle(active[0].Device.Volume.Id, 0);
            ObservedThroughput = Math.Max(0, _measuredSpeed.GetValueOrDefault(active[0].Device.Volume.Id));
            return;
        }

        long total = active.Sum(j => _measuredSpeed.GetValueOrDefault(j.Device.Volume.Id));
        if (total > 0) ObservedThroughput = total;

        // Y aqui esta lo importante, y es una negacion: NO se le recorta la velocidad a
        // nadie. Se podria pensar que repartir el ancho de banda lo iguala todo, pero es
        // justo al reves. El techo se calculaba con la velocidad ya medida, de modo que
        // en cuanto un dispositivo tenia un tropiezo el techo bajaba, y ya no volvia a
        // subir: el recorte impide que la maquina vuelva a ir mas rapido que el recorte.
        // Medido con el banco, cuatro destinos a la vez se quedaban clavados en 25 MB/s
        // cada uno mientras un destino solo hacia el mismo archivo a 155 MB/s.
        //
        // Repartir no es recortar. El ancho de banda se reparte solo, y mejor: el motor
        // lee el origen una vez y reparte los bloques entre los destinos, y las colas de
        // disco de Windows se ordenan solas. Poner un techo solo puede hacer la copia
        // mas lenta; nunca mas rapida.
        foreach (var j in active) engine.SetThrottle(j.Device.Volume.Id, 0);
    }

    /// <summary>
    /// Reparto proporcional. Se conserva el calculo por si mas adelante se quiere un
    /// techo de velocidad de verdad, pero en esta version no se usa para recortar:
    /// poner un techo medido con lo que ya ha pasado solo puede bajar el conjunto.
    /// </summary>
    private double WeightFor(CopyJob job)
    {
        string id = job.Device.Volume.Id;
        long remaining = Math.Max(0, job.TotalToProcess - job.BytesCopied);

        double w = Strategy switch
        {
            // El que menos le falta, mas banda: termina primero.
            PriorityStrategy.LeastRemaining => remaining,

            // Para que todos avancen el mismo porcentaje, el que tiene mas GB
            // pendientes necesita mas ancho de banda.
            PriorityStrategy.ByPercentage => Math.Max(1, job.TotalToProcess),

            // Quien ya va rapido recibe mas: se aprovechan los dispositivos rapidos.
            PriorityStrategy.AverageSpeed => _measuredSpeed.GetValueOrDefault(id),

            _ => remaining,
        };

        if (w <= 0) w = 1;
        return w;
    }

    private static long ApplyPriority(long share, PriorityLevel level) => level switch
    {
        PriorityLevel.Immediate => (long)(share * 4.0),
        PriorityLevel.High => (long)(share * 2.0),
        PriorityLevel.AboveNormal => (long)(share * 1.4),
        PriorityLevel.BelowNormal => (long)(share * 0.7),
        PriorityLevel.Low => (long)(share * 0.4),
        _ => share,
    };
}
