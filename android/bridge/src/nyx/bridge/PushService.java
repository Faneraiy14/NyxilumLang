package nyx.bridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

// "⚡ Миттєва доставка": foreground-сервіс тримає WebSocket (WsClient) з
// адресою, яку дала програма (PUSH_START <url>). Кожне текстове повідомлення
// сервера, крім "hello"/"ping", стає подією "push <текст>" у програмі
// (окремий процес, як SyncJob). "denied" - токен уже недійсний: сервіс
// зупиняється сам. Обрив - перепідключення з паузою 5 -> 60 с.
// Android вимагає для такого сервісу видиме сповіщення - воно тихе (MIN).
public final class PushService extends Service {
    private static final String TAG = "NxPush";
    private static final String PREFS = "nx_push";
    private static final String KEY_URL = "url";
    private static final int NOTIF_ID = 7001;

    private volatile boolean running;
    private volatile WsClient client;
    private Thread loop;

    private static SharedPreferences prefs(Context ctx) {
        return ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    static boolean enabled(Context ctx) {
        return prefs(ctx).getString(KEY_URL, null) != null;
    }

    static void start(Context ctx, String url) {
        prefs(ctx).edit().putString(KEY_URL, url).commit();
        startIfEnabled(ctx);
    }

    static void startIfEnabled(Context ctx) {
        if (!enabled(ctx)) return;
        try {
            ctx.startForegroundService(new Intent(ctx, PushService.class));
        } catch (Exception e) {
            // Android 12+ не дає стартувати з фону без винятку з оптимізації батареї
            Log.w(TAG, "не вдалося запустити сервіс: " + e);
        }
    }

    static void stop(Context ctx) {
        prefs(ctx).edit().remove(KEY_URL).commit();
        ctx.stopService(new Intent(ctx, PushService.class));
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Notification n = notification();
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        } else {
            startForeground(NOTIF_ID, n);
        }
        if (!enabled(this)) {
            stopSelf();
            return START_NOT_STICKY;
        }
        if (loop == null || !loop.isAlive()) {
            running = true;
            loop = new Thread(this::run, "nx-push");
            loop.start();
        }
        return START_STICKY;
    }

    private Notification notification() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm.getNotificationChannel("nx_push") == null) {
            NotificationChannel ch = new NotificationChannel("nx_push", "Зв'язок із сервером", NotificationManager.IMPORTANCE_MIN);
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
        String label = String.valueOf(getApplicationInfo().loadLabel(getPackageManager()));
        return new Notification.Builder(this, "nx_push")
                .setSmallIcon(android.R.drawable.stat_notify_sync_noanim)
                .setContentTitle(label + " на зв'язку")
                .setContentText("Миттєва доставка увімкнена")
                .setOngoing(true)
                .build();
    }

    private void run() {
        int backoff = 5;
        while (running) {
            String url = prefs(this).getString(KEY_URL, null);
            if (url == null) break;
            try {
                client = new WsClient(url, 75000);
                Log.i(TAG, "з'єднано");
                backoff = 5;
                // одразу після (пере)підключення - синхронізація: поки зв'язку не
                // було, могли статися зміни
                fire("sync");
                client.listen(text -> {
                    if (text.equals("denied")) {
                        Log.w(TAG, "сервер відмовив (токен недійсний) - зупиняюсь");
                        prefs(this).edit().remove(KEY_URL).commit();
                        running = false;
                        client.close();
                    } else if (!text.equals("hello") && !text.equals("ping")) {
                        fire(text);
                    }
                });
            } catch (Exception e) {
                Log.i(TAG, "обрив: " + e.getMessage());
            } finally {
                if (client != null) client.close();
            }
            if (!running) break;
            try {
                Thread.sleep(backoff * 1000L);
            } catch (InterruptedException ie) {
                break;
            }
            backoff = Math.min(backoff * 2, 60);
        }
        stopSelf();
    }

    private void fire(String text) {
        final Context app = getApplicationContext();
        new Thread(() -> NxBridge.runOnce(app, "push", text)).start();
    }

    @Override
    public void onDestroy() {
        running = false;
        if (client != null) client.close();
        if (loop != null) loop.interrupt();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
