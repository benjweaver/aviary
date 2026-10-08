namespace Aviary.Core;

public static class GuestRecommendations
{
    public static string For(string os, VmEngine engine, bool whpx, bool hyperVAvailable, bool glDevice)
    {
        if (os == "Windows") return (hyperVAvailable ? "Recommended: Hyper-V for Windows 11, with UEFI, TPM and native networking. " : "Windows 11 needs UEFI and TPM; enable Hyper-V before creating this guest. ") +
            "Windows 3D acceleration is not automatic, and Aviary does not configure Hyper-V GPU sharing yet. Adaptive VirtIO display is a display driver, not DirectX acceleration. Local-account setup is selected by default.";
        return (engine == VmEngine.HyperV ? "Hyper-V provides hardware virtualization and native networking. " : whpx ? "Recommended: WHPX, adaptive display and VirtIO networking. " : "Software emulation is available; enable WHPX for better CPU performance. ") +
            (engine == VmEngine.Qemu && glDevice ? "This QEMU build advertises VirGL. Linux 3D remains experimental and depends on host OpenGL and guest Mesa; device detection alone does not verify acceleration." : "Use software graphics for this configuration. Host GPU acceleration has not been verified.");
    }
}
