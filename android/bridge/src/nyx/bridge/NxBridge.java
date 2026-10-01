package nyx.bridge;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.provider.AlarmClock;
import android.util.Log;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

// Універсальний перекладач NyxilumLang <-> Android.
//
// Уся логіка застосунку - у програмі на NyxilumLang, скомпільованій у
// ARM64 і покладеній в APK як lib/arm64-v8a/libnxapp.so (Android дозволяє
// запускати файли лише з теки нативних бібліотек). Цей клас запускає її і
// розмовляє рядками через stdin/stdout - протокол описано в lib/android.nx.
// Тут немає НІЧОГО про будильники чи Маяк: лише виконання команд
// (екран, AlarmManager, сховище, HTTP) і пересилання подій.
public final class NxBridge {
    private static final String TAG = "NxBridge";

    // Те, що вміє показати екран (ScreenActivity); для фонових подій - null
    public interface Screen {
        void apply(List<String[]> uiCommands);
        void toast(String text);
        void finishScreen();
        void openUrl(String url);
        void sound(boolean on);
        void vibrate(boolean on);
    }

    private final Context ctx;
    private final Process proc;
    private final Writer out;
    private final BufferedReader in;

    public NxBridge(Context context) throws Exception {
        ctx = context.getApplicationContext();
        File exe = new File(ctx.getApplicationInfo().nativeLibraryDir, "libnxapp.so");
        proc = new ProcessBuilder(exe.getAbsolutePath()).redirectErrorStream(false).start();
        out = new OutputStreamWriter(proc.getOutputStream(), StandardCharsets.UTF_8);
        in = new BufferedReader(new InputStreamReader(proc.getInputStream(), StandardCharsets.UTF_8));
        drainStderr(proc.getErrorStream());
    }

    // Надіслати подію і виконувати команди програми до DONE.
    // Викликати НЕ з головного потоку (тут може бути мережа).
    public synchronized void event(Screen screen, String... fields) throws Exception {
        send(prepend("EVENT", fields));
        List<String[]> ui = new ArrayList<>();
        String line;
        while ((line = in.readLine()) != null) {
            String[] f = parse(line);
            String cmd = f[0];
            switch (cmd) {
                case "DONE":
                    if (screen != null && !ui.isEmpty()) screen.apply(ui);
                    return;
                case "UI_CLEAR": case "UI_TITLE": case "UI_TEXT": case "UI_BUTTON": case "UI_INPUT":
                    ui.add(f);
                    break;
                case "TOAST":
                    if (screen != null) screen.toast(arg(f, 1));
                    break;
                case "FINISH":
                    if (screen != null) screen.finishScreen();
                    break;
                case "OPEN_URL":
                    if (screen != null) screen.openUrl(arg(f, 1));
                    break;
                case "SOUND":
                    if (screen != null) screen.sound(isTrue(arg(f, 1)));
                    break;
                case "VIBRATE":
                    if (screen != null) screen.vibrate(isTrue(arg(f, 1)));
                    break;
                case "LOG":
                    Log.i(TAG, arg(f, 1));
                    break;
                case "ALARM_SET":
                    setAlarm(ctx, arg(f, 1), (long) (Double.parseDouble(arg(f, 2)) * 1000), arg(f, 3));
                    break;
                case "ALARM_CANCEL":
                    cancelAlarm(ctx, arg(f, 1));
                    break;
                case "CLOCK_ALARM":
                    clockAlarm(ctx, (int) Double.parseDouble(arg(f, 1)), (int) Double.parseDouble(arg(f, 2)), arg(f, 3), arg(f, 4));
                    break;
                case "CLOCK_TIMER":
                    clockTimer(ctx, (int) Double.parseDouble(arg(f, 1)), arg(f, 2));
                    break;
                case "CLOCK_DISMISS":
                    clockDismiss(ctx, arg(f, 1));
                    break;
                // те саме, але "Годинник" відкриває СИСТЕМА у вказаний час (секунди Unix) -
                // з фону Android 14+ не дає застосунку відкривати чужі екрани напряму
                case "CLOCK_ALARM_AT":
                    clockAt(ctx, "a:" + arg(f, 1), (long) (Double.parseDouble(arg(f, 2)) * 1000),
                            clockAlarmIntent((int) Double.parseDouble(arg(f, 3)), (int) Double.parseDouble(arg(f, 4)), arg(f, 5), ""));
                    break;
                case "CLOCK_TIMER_AT":
                    clockAt(ctx, "t:" + arg(f, 1), (long) (Double.parseDouble(arg(f, 2)) * 1000),
                            clockTimerIntent((int) Double.parseDouble(arg(f, 3)), arg(f, 4)));
                    break;
                case "CLOCK_DISMISS_TIMER_AT":
                    clockAt(ctx, "d:" + arg(f, 1), (long) (Double.parseDouble(arg(f, 2)) * 1000),
                            new Intent(AlarmClock.ACTION_DISMISS_TIMER));
                    break;
                case "CLOCK_AT_CANCEL":
                    clockAtCancel(ctx, arg(f, 1));
                    break;
                case "CLOCK_DISMISS_TIMER":
                    // офіційне "зупинити таймер" (API 28); чи слухає - залежить від "Годинника"
                    startClock(ctx, new Intent(AlarmClock.ACTION_DISMISS_TIMER), "зупинка таймера");
                    break;
                case "WAKE_AT":
                    WakeReceiver.set(ctx, arg(f, 1), (long) (Double.parseDouble(arg(f, 2)) * 1000));
                    break;
                case "WAKE_CANCEL":
                    WakeReceiver.cancel(ctx, arg(f, 1));
                    break;
                case "PUSH_START":
                    PushService.start(ctx, arg(f, 1));
                    break;
                case "PUSH_STOP":
                    PushService.stop(ctx);
                    break;
                case "BATTERY_ASK":
                    askBattery(ctx);
                    break;
                case "OVERLAY_ASK":
                    Overlay.ask(ctx);
                    break;
                case "OVERLAY_OK":
                    send(new String[]{"REPLY", Overlay.allowed(ctx) ? "1" : "0"});
                    break;
                case "SHIZUKU_STATE":
                    send(new String[]{"REPLY", ShizukuDoor.state(ctx)});
                    break;
                case "SHIZUKU_ASK":
                    ShizukuDoor.ask(ctx);
                    break;
                case "APPFN": {
                    String r = ShizukuDoor.appFunction(ctx, arg(f, 1), arg(f, 2), arg(f, 3));
                    send(new String[]{"REPLY", r});
                    break;
                }
                case "CLOCK_SHOW":
                    startClock(ctx, new Intent(AlarmClock.ACTION_SHOW_ALARMS), "список будильників");
                    break;
                case "TZ": {
                    // зсув часового поясу ТЕЛЕФОНА від UTC у секундах (з літнім часом)
                    long nowMs = System.currentTimeMillis();
                    send(new String[]{"REPLY", String.valueOf(java.util.TimeZone.getDefault().getOffset(nowMs) / 1000)});
                    break;
                }
                case "NOTIFY":
                    notifyInfo(ctx, arg(f, 1), arg(f, 2), arg(f, 3), arg(f, 4));
                    break;
                case "STORE_SET":
                    prefs().edit().putString(arg(f, 1), arg(f, 2)).apply();
                    break;
                case "STORE_REMOVE":
                    prefs().edit().remove(arg(f, 1)).apply();
                    break;
                case "STORE_GET": {
                    String v = prefs().getString(arg(f, 1), null);
                    send(v == null ? new String[]{"REPLY"} : new String[]{"REPLY", v});
                    break;
                }
                case "HTTP_GET":
                    send(http("GET", arg(f, 1), null));
                    break;
                case "HTTP_POST":
                    send(http("POST", arg(f, 1), arg(f, 2)));
                    break;
                default:
                    Log.w(TAG, "невідома команда: " + cmd);
            }
        }
        throw new Exception("програма завершилась, не надіславши DONE");
    }

    public void close() {
        try { out.close(); } catch (Exception ignored) { }
        proc.destroy();
    }

    // Одна подія з окремим процесом: для фонових подій (будильник після
    // перезавантаження, синхронізація)
    // Фонові події йдуть по одній (push, wake, sync можуть збігтися в часі,
    // а програма читає/пише одне сховище)
    private static final Object ONCE_LOCK = new Object();

    public static void runOnce(Context ctx, String... fields) {
        runOnceWith(ctx, null, fields);
    }

    // screen - що вміє "екран" фонової події (напр. лише спливаючі повідомлення)
    public static void runOnceWith(Context ctx, Screen screen, String... fields) {
        synchronized (ONCE_LOCK) {
            runOnceLocked(ctx, screen, fields);
        }
    }

    private static void runOnceLocked(Context ctx, Screen screen, String... fields) {
        NxBridge b = null;
        try {
            b = new NxBridge(ctx);
            b.event(screen, fields);
        } catch (Exception e) {
            Log.e(TAG, "фонова подія " + fields[0] + " впала", e);
        } finally {
            if (b != null) b.close();
        }
    }

    // ------------------------------------------------------------ будильник

    static void setAlarm(Context ctx, String id, long atMillis, String label) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        PendingIntent show = PendingIntent.getActivity(ctx, 0,
                new Intent(ctx, ScreenActivity.class), PendingIntent.FLAG_IMMUTABLE);
        am.setAlarmClock(new AlarmManager.AlarmClockInfo(atMillis, show), alarmIntent(ctx, id, label));
    }

    static void cancelAlarm(Context ctx, String id) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        am.cancel(alarmIntent(ctx, id, ""));
    }

    // Коли будильник спрацює, система розбудить AlarmReceiver (той покаже
    // повноекранне повідомлення -> ScreenActivity з подією "alarm <id>")
    private static PendingIntent alarmIntent(Context ctx, String id, String label) {
        Intent i = new Intent(ctx, AlarmReceiver.class)
                .setAction("nyx.ALARM." + id)
                .putExtra(ScreenActivity.EXTRA_ARG, id)
                .putExtra("nx.label", label);
        return PendingIntent.getBroadcast(ctx, id.hashCode(), i,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    // ------------------------------------------------------------ вбудований "Годинник"

    // Будильник у СИСТЕМНОМУ годиннику телефона (AlarmClock.ACTION_SET_ALARM):
    // дзвонить сам "Годинник" - з усіма його налаштуваннями (мелодія,
    // "Не турбувати", режим сну). days - "1,2,..." (1 = неділя, як Calendar)
    // або "" для одноразового.
    static void clockAlarm(Context ctx, int hour, int minute, String label, String days) {
        startClock(ctx, clockAlarmIntent(hour, minute, label, days), "будильник");
    }

    static Intent clockAlarmIntent(int hour, int minute, String label, String days) {
        Intent i = new Intent(AlarmClock.ACTION_SET_ALARM)
                .putExtra(AlarmClock.EXTRA_HOUR, hour)
                .putExtra(AlarmClock.EXTRA_MINUTES, minute)
                .putExtra(AlarmClock.EXTRA_MESSAGE, label)
                .putExtra(AlarmClock.EXTRA_VIBRATE, true)
                .putExtra(AlarmClock.EXTRA_SKIP_UI, true);
        if (days != null && !days.isEmpty()) {
            ArrayList<Integer> d = new ArrayList<>();
            for (String p : days.split(",")) d.add(Integer.parseInt(p.trim()));
            i.putExtra(AlarmClock.EXTRA_DAYS, d);
        }
        return i;
    }

    static void clockTimer(Context ctx, int seconds, String label) {
        startClock(ctx, clockTimerIntent(seconds, label), "таймер");
    }

    static Intent clockTimerIntent(int seconds, String label) {
        return new Intent(AlarmClock.ACTION_SET_TIMER)
                .putExtra(AlarmClock.EXTRA_LENGTH, seconds)
                .putExtra(AlarmClock.EXTRA_MESSAGE, label)
                .putExtra(AlarmClock.EXTRA_SKIP_UI, true);
    }

    // "Конверт" для системи: о atMillis AlarmManager САМ відкриє intent (екран
    // "Годинника"). Android 14+ блокує запуск чужих екранів із фону
    // (BAL_BLOCK), але PendingIntent системного будильника (setAlarmClock) з
    // дозволом творця - дозволено. key - щоб замінити/скасувати той самий конверт.
    static void clockAt(Context ctx, String key, long atMillis, Intent intent) {
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        android.os.Bundle opts = null;
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            android.app.ActivityOptions o = android.app.ActivityOptions.makeBasic();
            o.setPendingIntentCreatorBackgroundActivityStartMode(android.app.ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED);
            opts = o.toBundle();
        }
        PendingIntent pi = PendingIntent.getActivity(ctx, ("clock:" + key).hashCode(), intent,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT, opts);
        PendingIntent show = PendingIntent.getActivity(ctx, 2,
                new Intent(AlarmClock.ACTION_SHOW_ALARMS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE);
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        am.setAlarmClock(new AlarmManager.AlarmClockInfo(Math.max(atMillis, System.currentTimeMillis() + 1000), show), pi);
        Log.i(TAG, "Годинник: конверт " + key + " на " + new java.util.Date(atMillis));
    }

    // id - як у CLOCK_ALARM_AT ("a:"), CLOCK_TIMER_AT ("t:"), CLOCK_DISMISS_TIMER_AT ("d:")
    static void clockAtCancel(Context ctx, String key) {
        String[] actions = {AlarmClock.ACTION_SET_ALARM, AlarmClock.ACTION_SET_TIMER, AlarmClock.ACTION_DISMISS_TIMER};
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        for (String action : actions) {
            PendingIntent pi = PendingIntent.getActivity(ctx, ("clock:" + key).hashCode(),
                    new Intent(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                    PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_NO_CREATE);
            if (pi != null) {
                am.cancel(pi);
                pi.cancel();
            }
        }
    }

    // Вимкнути будильник "Годинника" за назвою (підтримують не всі годинники)
    static void clockDismiss(Context ctx, String label) {
        startClock(ctx, new Intent(AlarmClock.ACTION_DISMISS_ALARM)
                .putExtra(AlarmClock.EXTRA_ALARM_SEARCH_MODE, AlarmClock.ALARM_SEARCH_MODE_LABEL)
                .putExtra(AlarmClock.EXTRA_MESSAGE, label), "вимкнення будильника");
    }

    // Системне вікно "не оптимізувати батарею" (раз; якщо вже дозволено - нічого).
    // Без цього Android у сні обриває з'єднання ⚡ і не дає запускати сервіс з фону.
    static void askBattery(Context ctx) {
        android.os.PowerManager pm = (android.os.PowerManager) ctx.getSystemService(Context.POWER_SERVICE);
        if (pm.isIgnoringBatteryOptimizations(ctx.getPackageName())) return;
        try {
            ctx.startActivity(new Intent(android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                    .setData(android.net.Uri.parse("package:" + ctx.getPackageName()))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception e) {
            Log.w(TAG, "вікно батареї не відкрилось: " + e);
        }
    }

    private static void startClock(Context ctx, Intent i, String what) {
        // Android 14+ блокує запуск чужих екранів із фону (BAL_BLOCK), але
        // дозволяє застосунку з видимим вікном "поверх інших" - тож якщо людина
        // дала цей дозвіл, запускаємо через мить-вікно (Overlay)
        if (Overlay.allowed(ctx)) {
            Overlay.startWithOverlay(ctx, i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), what);
            return;
        }
        try {
            ctx.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            Log.i(TAG, "Годинник: " + what + " - запит надіслано");
        } catch (Exception e) {
            Log.e(TAG, "Годинник: " + what + " не вдалося", e);
        }
    }

    // Звичайне сповіщення (id - щоб оновлювати те саме); натискання
    // відкриває список будильників "Годинника"
    // url - що відкрити натисканням (напр. сторінку нової версії); порожній -
    // список будильників "Годинника"
    static void notifyInfo(Context ctx, String id, String title, String text, String url) {
        android.app.NotificationManager nm = (android.app.NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm.getNotificationChannel("nx_info") == null)
            nm.createNotificationChannel(new android.app.NotificationChannel("nx_info", "Повідомлення", android.app.NotificationManager.IMPORTANCE_DEFAULT));
        Intent open = url == null || url.isEmpty()
                ? new Intent(AlarmClock.ACTION_SHOW_ALARMS)
                : new Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url));
        PendingIntent pi = PendingIntent.getActivity(ctx, ("info:" + id).hashCode(),
                open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        android.app.Notification n = new android.app.Notification.Builder(ctx, "nx_info")
                .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(new android.app.Notification.BigTextStyle().bigText(text))
                .setContentIntent(pi)
                .setAutoCancel(true)
                .build();
        nm.notify(("info:" + id).hashCode(), n);
    }

    // ------------------------------------------------------------ мережа, сховище

    private SharedPreferences prefs() {
        return ctx.getSharedPreferences("nx", Context.MODE_PRIVATE);
    }

    private static String[] http(String method, String url, String body) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setRequestMethod(method);
            c.setConnectTimeout(15000);
            c.setReadTimeout(20000);
            if (body != null) {
                c.setDoOutput(true);
                c.setRequestProperty("Content-Type", "text/plain; charset=utf-8");
                try (OutputStream os = c.getOutputStream()) {
                    os.write(body.getBytes(StandardCharsets.UTF_8));
                }
            }
            int code = c.getResponseCode();
            InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
            return new String[]{"REPLY", String.valueOf(code), is == null ? "" : readAll(is)};
        } catch (Exception e) {
            return new String[]{"REPLY", "0", String.valueOf(e.getMessage())};
        } finally {
            if (c != null) c.disconnect();
        }
    }

    private static String readAll(InputStream is) throws Exception {
        ByteArrayOutputStream buf = new ByteArrayOutputStream();
        byte[] chunk = new byte[8192];
        int n;
        while ((n = is.read(chunk)) > 0) buf.write(chunk, 0, n);
        return new String(buf.toByteArray(), StandardCharsets.UTF_8);
    }

    // ------------------------------------------------------------ протокол

    private void send(String[] fields) throws Exception {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < fields.length; i++) {
            if (i > 0) sb.append('\t');
            sb.append(escape(fields[i]));
        }
        sb.append('\n');
        out.write(sb.toString());
        out.flush();
    }

    static String escape(String s) {
        return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n");
    }

    static String unescape(String s) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 1 < s.length()) {
                char n = s.charAt(++i);
                sb.append(n == 't' ? '\t' : n == 'n' ? '\n' : n);
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    static String[] parse(String line) {
        String[] raw = line.split("\t", -1);
        for (int i = 0; i < raw.length; i++) raw[i] = unescape(raw[i]);
        return raw;
    }

    private static String arg(String[] f, int i) {
        return i < f.length ? f[i] : "";
    }

    private static boolean isTrue(String s) {
        return s.equalsIgnoreCase("true") || s.equals("1");
    }

    private static String[] prepend(String first, String[] rest) {
        String[] r = new String[rest.length + 1];
        r[0] = first;
        System.arraycopy(rest, 0, r, 1, rest.length);
        return r;
    }

    // stderr програми (помилки рантайму) -> logcat, щоб не забив буфер
    private static void drainStderr(final InputStream err) {
        new Thread(() -> {
            try (BufferedReader r = new BufferedReader(new InputStreamReader(err, StandardCharsets.UTF_8))) {
                String l;
                while ((l = r.readLine()) != null) Log.e(TAG, "nx: " + l);
            } catch (Exception ignored) { }
        }).start();
    }
}
