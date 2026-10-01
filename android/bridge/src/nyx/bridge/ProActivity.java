package nyx.bridge;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;
import android.widget.Toast;

// Окрема НЕПРОЗОРА активність лише для вмикання "про"-режиму: Shizuku показує
// діалог згоди тільки над справжнім (непрозорим) переднім вікном, а головний
// екран застосунку - прозорий (--headless). Відкривається посиланням
// mayak://pro (ScreenActivity перенаправляє сюди).
public final class ProActivity extends Activity {
    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        TextView tv = new TextView(this);
        tv.setText("Вмикаю повний контроль над «Годинником»…\n\nДозволь доступ у вікні Shizuku, що зараз з'явиться.");
        tv.setTextSize(18);
        int pad = (int) (24 * getResources().getDisplayMetrics().density);
        tv.setPadding(pad, pad, pad, pad);
        setContentView(tv);
        ShizukuDoor.requestInteractive(this, ok -> {
            if (ok) {
                Toast.makeText(getApplicationContext(),
                        "Маяк: повний контроль увімкнено ✅ Будильники й таймери тепер ідуть прямо в «Годинник».",
                        Toast.LENGTH_LONG).show();
                new Thread(() -> NxBridge.runOnceWith(getApplicationContext(),
                        new BackgroundScreen(getApplicationContext()), "sync", "")).start();
            } else {
                Toast.makeText(getApplicationContext(),
                        "Маяк: повний контроль не увімкнено. Переконайся, що Shizuku запущено, і спробуй ще раз.",
                        Toast.LENGTH_LONG).show();
            }
            finish();
        });
    }
}
