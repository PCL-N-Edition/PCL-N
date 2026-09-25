namespace Nexa.Jvm.Host;

internal static class Program
{
    private static int Main(string[] args) => args is ["--jvm-host"] ? NativeJvmHost.Run() : 2;
}
