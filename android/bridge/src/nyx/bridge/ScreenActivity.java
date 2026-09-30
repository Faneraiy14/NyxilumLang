package nyx.bridge;

import android.app.Activity;
import android.app.job.JobInfo;
import android.app.job.JobScheduler;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.media.AudioAttributes;
import android.media.Ringtone;
import android.media.RingtoneManager;
import android.net.Uri;
import android.os.Bundle;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

// Єдиний екран застосунку: малює те, що описала програма на NyxilumLang
// (UI_TITLE/UI_TEXT/UI_BUTTON/UI_INPUT), і пересилає їй натискання.
// Відкривається звичайно (подія start), посиланням (deeplink) або
// будильником (alarm) - тоді показується поверх блокування екрана.
public final class ScreenActivity extends Activity implements NxBridge.Screen {
    static final String EXTRA_EVENT = "nx.event";
    static final String EXTRA_ARG = "nx.arg";

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private NxBridge bridge;
    private LinearLayout root;
    private final Map<String, EditText> inputs = new LinkedHashMap<>();
    private Ringtone ringtone;
    private Vibrator vibrator;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(20);
        root.setPadding(pad, pad, pad, pad);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(root);
        // Android 15+ малює вміст під рядком стану й навігацією - відступаємо
        scroll.setOnApplyWindowInsetsListener((v, insets) -> {
            android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            return WindowInsets.CONSUMED;
        });
        setContentView(scroll);
        scheduleSync(this);
        // Android 13+: без дозволу на сповіщення будильник не зможе показати
        // повноекранне вікно - просимо один раз
        if (checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, 1);
        dispatch(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        dispatch(intent);
    }

    private void dispatch(Intent intent) {
        String event = intent.getStringExtra(EXTRA_EVENT);
        String arg = intent.getStringExtra(EXTRA_ARG);
        Uri data = intent.getData();
        if (event == null && data != null) {
            event = "deeplink";
            arg = data.toString();
        }
        if (event == null) event = "start";
        if (event.equals("alarm")) {
            // поверх блокування, екран увімкнено й не гасне; далі звук веде
            // сама програма (alarmSound), тож повідомлення з мелодією прибираємо
            setShowWhenLocked(true);
            setTurnScreenOn(true);
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            if (arg != null) AlarmReceiver.cancel(this, arg);
        }
        send(event, arg == null ? "" : arg);
    }

    private void send(final String... fields) {
        worker.execute(() -> {
            try {
                if (bridge == null) bridge = new NxBridge(this);
                bridge.event(this, fields);
            } catch (Exception e) {
                if (bridge != null) bridge.close();
                bridge = null;
                toast("Помилка застосунку: " + e.getMessage());
            }
        });
    }

    // ------------------------------------------------------------ NxBridge.Screen

    @Override
    public void apply(final List<String[]> ui) {
        runOnUiThread(() -> {
            for (String[] f : ui) {
                switch (f[0]) {
                    case "UI_CLEAR":
                        root.removeAllViews();
                        inputs.clear();
                        break;
                    case "UI_TITLE": {
                        TextView t = text(f.length > 1 ? f[1] : "", 26);
                        t.setTypeface(Typeface.DEFAULT_BOLD);
                        root.addView(t);
                        break;
                    }
                    case "UI_TEXT":
                        root.addView(text(f.length > 2 ? f[2] : "", 17));
                        break;
                    case "UI_BUTTON": {
                        final String id = f.length > 1 ? f[1] : "";
                        Button b = new Button(this);
                        b.setText(f.length > 2 ? f[2] : id);
                        b.setAllCaps(false);
                        b.setOnClickListener(v -> click(id));
                        root.addView(b);
                        break;
                    }
                    case "UI_INPUT": {
                        EditText e = new EditText(this);
                        e.setHint(f.length > 2 ? f[2] : "");
                        e.setText(f.length > 3 ? f[3] : "");
                        e.setSingleLine(true);
                        inputs.put(f.length > 1 ? f[1] : "", e);
                        root.addView(e);
                        break;
                    }
                }
            }
        });
    }

    private void click(String id) {
        List<String> fields = new ArrayList<>();
        fields.add("click");
        fields.add(id);
        for (Map.Entry<String, EditText> e : inputs.entrySet()) {
            fields.add(e.getKey());
            fields.add(e.getValue().getText().toString());
        }
        send(fields.toArray(new String[0]));
    }

    @Override
    public void toast(final String text) {
        runOnUiThread(() -> Toast.makeText(this, text, Toast.LENGTH_LONG).show());
    }

    @Override
    public void finishScreen() {
        runOnUiThread(this::finish);
    }

    @Override
    public void openUrl(final String url) {
        runOnUiThread(() -> startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url))));
    }

    // Мелодія будильника на гучності будильника (не медіа й не дзвінка)
    @Override
    public void sound(final boolean on) {
        runOnUiThread(() -> {
            if (ringtone != null) {
                ringtone.stop();
                ringtone = null;
            }
            if (!on) return;
            Uri uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
            if (uri == null) uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE);
            ringtone = RingtoneManager.getRingtone(this, uri);
            if (ringtone == null) return;
            ringtone.setAudioAttributes(new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                    .build());
            ringtone.setLooping(true);
            ringtone.play();
        });
    }

    @Override
    public void vibrate(final boolean on) {
        runOnUiThread(() -> {
            if (vibrator == null) vibrator = (Vibrator) getSystemService(Context.VIBRATOR_SERVICE);
            if (vibrator == null) return;
            if (on) vibrator.vibrate(VibrationEffect.createWaveform(new long[]{0, 800, 600}, 0));
            else vibrator.cancel();
        });
    }

    @Override
    protected void onDestroy() {
        sound(false);
        if (vibrator != null) vibrator.cancel();
        if (ringtone != null) ringtone.stop();
        worker.execute(() -> {
            if (bridge != null) bridge.close();
        });
        worker.shutdown();
        super.onDestroy();
    }

    // ------------------------------------------------------------ допоміжне

    private TextView text(String s, int sp) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setPadding(0, dp(6), 0, dp(6));
        return t;
    }

    private int dp(int v) {
        return (int) (v * getResources().getDisplayMetrics().density);
    }

    // Фонова подія "sync" раз на ~15 хв (мінімум, який дозволяє Android)
    static void scheduleSync(Context ctx) {
        try {
            JobScheduler js = (JobScheduler) ctx.getSystemService(Context.JOB_SCHEDULER_SERVICE);
            if (js == null || js.getPendingJob(1) != null) return;
            js.schedule(new JobInfo.Builder(1, new ComponentName(ctx, SyncJob.class))
                    .setPeriodic(15 * 60 * 1000L)
                    .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                    .setPersisted(true)
                    .build());
        } catch (Exception e) {
            // без фонової синхронізації застосунок однаково працює
            android.util.Log.w("NxBridge", "не вдалося запланувати sync", e);
        }
    }
}
