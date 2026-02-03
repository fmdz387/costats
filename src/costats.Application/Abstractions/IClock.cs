namespace costats.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
