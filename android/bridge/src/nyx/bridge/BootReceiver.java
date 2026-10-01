package nyx.bridge;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

// Після перезавантаження Android забуває всі будильники: подія "boot"
// дає програмі поставити їх знову (зі свого сховища)
public final class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        // після перезавантаження і після оновлення застосунку (тоді Android теж
        // зупиняє сервіси й забуває пробудження)
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())
                && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(intent.getAction())) return;
        final PendingResult pending = goAsync();
        final Context app = ctx.getApplicationContext();
        new Thread(() -> {
            try {
                NxBridge.runOnce(app, "boot", "");
                ScreenActivity.scheduleSync(app);
                PushService.startIfEnabled(app);
            } finally {
                pending.finish();
            }
        }).start();
    }
}
