package nyx.bridge;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;

// Запускається Shizuku в окремому процесі з правами shell (uid 2000).
// Уміє рівно одне: виконати передану команду й повернути її вивід. Викликач
// (ShizukuDoor) пропускає лише "cmd app_function execute-app-function ...",
// тож навіть із правами shell цей сервіс не робить нічого іншого.
public final class AppFnService extends IAppFn.Stub {
    public AppFnService() { }
    public AppFnService(android.content.Context ctx) { }

    @Override
    public void destroy() {
        System.exit(0);
    }

    @Override
    public String run(String[] args) {
        try {
            Process p = new ProcessBuilder(args).redirectErrorStream(true).start();
            String out = readAll(p.getInputStream());
            p.waitFor();
            return out;
        } catch (Exception e) {
            return "ERR " + e.getMessage();
        }
    }

    private static String readAll(InputStream is) throws Exception {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        int n;
        while ((n = is.read(buf)) != -1) b.write(buf, 0, n);
        return b.toString("UTF-8");
    }
}
