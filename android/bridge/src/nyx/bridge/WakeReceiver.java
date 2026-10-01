package nyx.bridge;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

// Точне "пробудження" програми у вказаний час (WAKE_AT <id> <час>) - подія
// "wake <id>". Працює й у режимі сну (setExactAndAllowWhileIdle); після
// перезавантаження Android їх забуває - програма ставить знову на "boot".
public final class WakeReceiver extends BroadcastReceiver {
    static void set(Context ctx, String id, long atMillis) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, atMillis, intent(ctx, id));
    }

    static void cancel(Context ctx, String id) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        am.cancel(intent(ctx, id));
    }

    private static PendingIntent intent(Context ctx, String id) {
        Intent i = new Intent(ctx, WakeReceiver.class).setAction("nyx.WAKE." + id).putExtra("nx.id", id);
        return PendingIntent.getBroadcast(ctx, ("wake:" + id).hashCode(), i,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    @Override
    public void onReceive(Context ctx, Intent intent) {
        final String id = intent.getStringExtra("nx.id");
        if (id == null) return;
        final PendingResult pending = goAsync();
        final Context app = ctx.getApplicationContext();
        new Thread(() -> {
            try {
                NxBridge.runOnce(app, "wake", id);
            } finally {
                pending.finish();
            }
        }).start();
    }
}
