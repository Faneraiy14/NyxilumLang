package nyx.bridge;

import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.widget.Toast;

import java.util.List;

// "Екран" для події застосунку без інтерфейсу (--headless): нічого не
// малює, лише показує спливаючі повідомлення й відкриває посилання.
final class BackgroundScreen implements NxBridge.Screen {
    private final Context app;
    private final Handler main = new Handler(Looper.getMainLooper());

    BackgroundScreen(Context app) {
        this.app = app;
    }

    @Override public void apply(List<String[]> uiCommands) { }

    @Override
    public void toast(final String text) {
        main.post(() -> Toast.makeText(app, text, Toast.LENGTH_LONG).show());
    }

    @Override public void finishScreen() { }

    @Override
    public void openUrl(String url) {
        try {
            app.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception ignored) { }
    }

    @Override public void sound(boolean on) { }

    @Override public void vibrate(boolean on) { }
}
