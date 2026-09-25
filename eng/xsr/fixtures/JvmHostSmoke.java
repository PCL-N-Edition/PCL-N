public final class JvmHostSmoke {
    public static void main(String[] args) throws Exception {
        switch (args[0]) {
            case "throw": throw new IllegalStateException("NEXA_JNI_EXCEPTION");
            case "exit": System.exit(7); break;
            case "wait":
                System.out.println("NEXA_JNI_WAITING");
                Thread.sleep(60000);
                break;
            default:
                if (args.length != 3 || !args[1].equals("") || !args[2].equals("中文😀"))
                    throw new AssertionError("argument roundtrip failed");
                if (!System.getProperty("nexa.fixture").equals("test value"))
                    throw new AssertionError("VM option missing");
                new Thread(() -> {
                    try { Thread.sleep(200); } catch (InterruptedException e) { throw new RuntimeException(e); }
                    System.out.println("NEXA_JNI_BACKGROUND_FINISHED");
                }).start();
                System.out.println("NEXA_JNI_MAIN_RETURNED");
                System.err.println("NEXA_JNI_STDERR");
        }
    }
}
