namespace ServerManager.Core;

public static class PortPolicy
{
    public static int Validate(int port, string? parameterName = null)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(port),
                "Ports must be between 1 and 65535.");
        }

        return port;
    }

    public static void ValidateDistinct(params int[] ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        foreach (var port in ports)
        {
            Validate(port);
        }

        if (ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("Configured ports must be distinct.", nameof(ports));
        }
    }
}
