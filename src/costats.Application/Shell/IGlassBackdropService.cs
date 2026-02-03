namespace costats.Application.Shell;

public interface IGlassBackdropService
{
    bool IsSupported { get; }

    void ApplyBackdrop(IntPtr hwnd);
}
