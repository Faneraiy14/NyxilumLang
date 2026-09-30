package nyx.bridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.RingtoneManager;
import android.net.Uri;

// Будильник спрацював. Android 14+ не дає застосунку у фоні самому
// відкривати вікна, тож робимо так, як системний "Годинник": повідомлення
// категорії ALARM з повноекранним вікном (full-screen intent). Коли телефон
// заблоковано - одразу відкривається ScreenActivity поверх блокування; коли
// ним користуються - спливає повідомлення. Мелодія будильника повторюється
// (FLAG_INSISTENT), доки не відкриють екран або не змахнуть повідомлення.
public final class AlarmReceiver extends BroadcastReceiver {
    static final String CHANNEL = "nx_alarm";

    @Override
    public void onReceive(Context ctx, Intent intent) {
        String id = intent.getStringExtra(ScreenActivity.EXTRA_ARG);
        String label = intent.getStringExtra("nx.label");
        if (id == null) return;
        NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        ensureChannel(nm);

        Intent open = new Intent(ctx, ScreenActivity.class)
                .setAction("nyx.ALARM." + id)
                .putExtra(ScreenActivity.EXTRA_EVENT, "alarm")
                .putExtra(ScreenActivity.EXTRA_ARG, id)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        PendingIntent pi = PendingIntent.getActivity(ctx, id.hashCode(), open,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);

        Notification n = new Notification.Builder(ctx, CHANNEL)
                .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
                .setContentTitle("⏰ " + (label == null || label.isEmpty() ? "Будильник" : label))
                .setContentText("Натисни, щоб відкрити")
                .setCategory(Notification.CATEGORY_ALARM)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .setFullScreenIntent(pi, true)
                .setContentIntent(pi)
                .setAutoCancel(true)
                .build();
        n.flags |= Notification.FLAG_INSISTENT;
        nm.notify(id.hashCode(), n);
    }

    static void ensureChannel(NotificationManager nm) {
        if (nm.getNotificationChannel(CHANNEL) != null) return;
        NotificationChannel ch = new NotificationChannel(CHANNEL, "Будильники", NotificationManager.IMPORTANCE_HIGH);
        Uri sound = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
        ch.setSound(sound, new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build());
        ch.enableVibration(true);
        ch.setVibrationPattern(new long[]{0, 800, 600, 800, 600});
        ch.setBypassDnd(true);
        ch.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
        nm.createNotificationChannel(ch);
    }

    static void cancel(Context ctx, String id) {
        NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        nm.cancel(id.hashCode());
    }
}
