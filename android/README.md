# Android-застосунки на NyxilumLang

Застосунок - звичайна програма на NyxilumLang, скомпільована в ARM64
(`nx compile-native --target android-arm64`). Фіксований **універсальний
перекладач** на Java (`bridge/`) запускає її і розмовляє рядками через
stdin/stdout; уся логіка - на NyxilumLang, через `lib/android.nx`.

```bash
android/build-apk.sh app.nx --package com.example.app --label "Назва" [--headless] [--scheme app]
adb install -r app.apk
```

Без Android Studio і Gradle: javac -> d8 -> aapt2 -> zipalign -> apksigner.
Потрібен Android SDK (`ANDROID_HOME`, типово `~/Android/Sdk`) з
`platforms;android-36` і `build-tools`.

Що вміє `lib/android.nx`:
- **Будильник/таймер у вбудованому «Годиннику»** (`clockAlarm`, `clockTimer`) -
  дзвонить сам системний годинник, крізь «Не турбувати» й режим сну.
  Перевірено на Samsung (Android 16): працює й **з фону**, при заблокованому
  телефоні. Вимкнути чи видалити будильник у «Годиннику» Android застосункам
  не дає (Samsung на `clockDismiss` лише відкриває список будильників).
- Власний будильник через AlarmManager (`alarmSet`) - повідомлення з
  повноекранним вікном; запасний варіант.
- Фонові події: `sync` (раз на ~15 хв), `boot` (після перезавантаження);
  перше відкриття після встановлення обов'язкове (так влаштований Android).
- Сховище (`storeGet/storeSet`), HTTP(S) (`netGet/netPost`).
- Інтерфейс (`uiTitle/uiText/uiButton/uiInput`) - готовий «макет»; з
  `--headless` екран прозорий і застосунок працює без інтерфейсу.

Приклад: `examples/hello.nx` (лічильник, будильники, таймер).
