package nyx.bridge;

import android.content.ComponentName;
import android.content.Context;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.os.IBinder;
import android.util.Log;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import rikka.shizuku.Shizuku;

// "Двері" до прав ADB через Shizuku (опційний "про"-режим): дозволяють
// викликати AppFunctions "Годинника" (створити/змінити/видалити будильник
// чи таймер по-справжньому). Без Shizuku застосунок працює як завжди.
//
// Стан: "none" - Shizuku не встановлено/не запущено; "denied" - не дано
// дозвіл; "ready" - можна виконувати. Виконується ЛИШЕ "cmd app_function".
public final class ShizukuDoor {
    private static final String TAG = "NxShizuku";
    private static final int PERM_CODE = 4411;

    private static volatile IAppFn service;
    private static ServiceConnection conn;
    private static volatile boolean binderReady;
    private static boolean listenerAdded;

    // Shizuku надсилає binder асинхронно після старту процесу - чекаємо його
    private static synchronized void ensureBinderListener() {
        if (listenerAdded) return;
        listenerAdded = true;
        try {
            Shizuku.addBinderReceivedListenerSticky(() -> binderReady = true);
            Shizuku.addBinderDeadListener(() -> binderReady = false);
        } catch (Throwable t) {
            Log.w(TAG, "binder listener: " + t);
        }
    }

    private static boolean waitBinder() {
        ensureBinderListener();
        for (int i = 0; i < 30 && !binderReady; i++) {
            if (alive()) { binderReady = true; break; }
            try { Thread.sleep(100); } catch (InterruptedException e) { return false; }
        }
        return binderReady || alive();
    }

    static boolean installed(Context ctx) {
        try {
            ctx.getPackageManager().getPackageInfo("moe.shizuku.privileged.api", 0);
            return true;
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    static boolean alive() {
        try {
            ensureBinderListener();
            return Shizuku.pingBinder();
        } catch (Throwable t) {
            return false;
        }
    }

    static boolean granted() {
        try {
            return alive() && !Shizuku.isPreV11() && Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED;
        } catch (Throwable t) {
            return false;
        }
    }

    static String state(Context ctx) {
        if (!installed(ctx)) return "none";
        if (!waitBinder()) return "none";
        return granted() ? "ready" : "denied";
    }

    static void ask(Context ctx) {
        try {
            if (waitBinder() && !granted()) Shizuku.requestPermission(PERM_CODE);
        } catch (Throwable t) {
            Log.w(TAG, "requestPermission: " + t);
        }
    }

    private static Shizuku.UserServiceArgs args(Context ctx) {
        return new Shizuku.UserServiceArgs(new ComponentName(ctx.getPackageName(), AppFnService.class.getName()))
                .daemon(false)
                .processNameSuffix("appfn")
                .version(1);
    }

    // Переконатись, що сервіс прив'язаний (чекає до 8 с)
    private static synchronized IAppFn ensure(Context ctx) {
        if (service != null) return service;
        if (!waitBinder() || !granted()) return null;
        final CountDownLatch latch = new CountDownLatch(1);
        conn = new ServiceConnection() {
            @Override public void onServiceConnected(ComponentName name, IBinder binder) {
                service = IAppFn.Stub.asInterface(binder);
                latch.countDown();
            }
            @Override public void onServiceDisconnected(ComponentName name) {
                service = null;
            }
        };
        try {
            Shizuku.bindUserService(args(ctx), conn);
            latch.await(8, TimeUnit.SECONDS);
        } catch (Throwable t) {
            Log.e(TAG, "bindUserService: " + t);
        }
        return service;
    }

    // Виконати AppFunction; повертає JSON-вивід або "ERR ..."
    static String appFunction(Context ctx, String pkg, String fn, String paramsJson) {
        IAppFn s = ensure(ctx);
        if (s == null) return "ERR not-ready";
        try {
            return s.run(new String[]{"cmd", "app_function", "execute-app-function",
                    "--package", pkg, "--function", fn, "--parameters", paramsJson});
        } catch (Throwable t) {
            service = null;
            return "ERR " + t.getMessage();
        }
    }
}
