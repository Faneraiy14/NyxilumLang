package nyx.bridge;

import android.content.Context;
import android.content.Intent;
import android.graphics.PixelFormat;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import android.view.View;
import android.view.WindowManager;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

// Запуск екрана іншого застосунку з фону через мить-вікно "поверх інших".
// Android 14+ (BAL) блокує фонові запуски, але дозволяє їх застосунку з
// ВИДИМИМ вікном TYPE_APPLICATION_OVERLAY (дозвіл "Поверх інших
// застосунків", людина вмикає сама). Вікно 1x1 піксель, без дотиків і
// фокусу, живе ~1.5 с: показали -> запустили -> прибрали.
public final class Overlay {
    private static final String TAG = "NxOverlay";

    static boolean allowed(Context ctx) {
        return Settings.canDrawOverlays(ctx);
    }

    // Системне вікно дозволу (з екрана застосунку)
    static void ask(Context ctx) {
        if (allowed(ctx)) return;
        try {
            ctx.startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:" + ctx.getPackageName())).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception e) {
            Log.w(TAG, "вікно дозволу не відкрилось: " + e);
        }
    }

    // Викликати НЕ з головного потоку (чекає, поки вікно з'явиться)
    static void startWithOverlay(final Context ctx, final Intent intent, final String what) {
        final Context app = ctx.getApplicationContext();
        final Handler main = new Handler(Looper.getMainLooper());
        final WindowManager wm = (WindowManager) app.getSystemService(Context.WINDOW_SERVICE);
        final View[] view = new View[1];
        final CountDownLatch shown = new CountDownLatch(1);
        main.post(() -> {
            try {
                View v = new View(app);
                v.setBackgroundColor(0x01000000);
                WindowManager.LayoutParams lp = new WindowManager.LayoutParams(1, 1,
                        WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                        WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE,
                        PixelFormat.TRANSLUCENT);
                lp.gravity = android.view.Gravity.TOP | android.view.Gravity.START;
                wm.addView(v, lp);
                view[0] = v;
                // вікно справді на екрані - лише після першого кадру
                v.post(shown::countDown);
            } catch (Exception e) {
                Log.e(TAG, "мить-вікно не показалось", e);
                shown.countDown();
            }
        });
        try {
            shown.await(2, TimeUnit.SECONDS);
            Thread.sleep(250);
        } catch (InterruptedException ignored) { }
        try {
            app.startActivity(intent);
            Log.i(TAG, "Годинник: " + what + " - запущено через мить-вікно");
        } catch (Exception e) {
            Log.e(TAG, "Годинник: " + what + " не вдалося", e);
        }
        main.postDelayed(() -> {
            if (view[0] != null) {
                try { wm.removeView(view[0]); } catch (Exception ignored) { }
            }
        }, 1500);
    }
}
