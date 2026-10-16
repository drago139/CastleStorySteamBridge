# Castle Story Steam Bridge

[English](#english) · [Українською](#українською)

---

## English

**Castle Story Steam Bridge** is a BepInEx 5 plugin for the original *Castle Story* (Steam, Unity 5.6, Mono) that lets players join each other over the internet through **Steam P2P**, with no IP addresses, port forwarding or VPN. It tunnels the game's UNET traffic through Steam, and adds a set of quality-of-life and stability features on top.

> Unofficial, free community mod. Not affiliated with Sauropod Studio. You need your own copy of Castle Story on Steam. This repository contains **no game files and no decompiled game code**.

### Features

- **Join over the internet via Steam P2P**: friends *and* strangers (public lobbies appear in the game's "Public server list" with a `[Mod]` prefix; a host code is a fallback). Steam relay is allowed when a direct route is impossible.
- **Host moderation**: Kick, Ban 10 min, Ban 60 min, ban list with "unban".
- **Separate unit-position channel (0.3.52+)**: the host sends unit positions on a dedicated unreliable Steam channel (10 Hz), so a delayed or lost packet in the main stream no longer freezes characters while their animation keeps running. Test with two players: 10 % packet loss, big jumps 35 → 1; no freezing while the main stream is blocked for 3 s. Needs the mod on both sides; falls back to the normal game sync otherwise.
- **Per-connection UNET timeout**: the bridge/host uses a long timeout, direct connections keep the game's own value, so stalls of several seconds no longer drop players.
- **Item and class filter (0.3.53)**: the host can switch off buildings, blocks and brickton classes (class kits) in a window with pictures; the list is sent to guests.
- **Visual-effect switches**: Halloween costumes/moon and winter snow/hats can be turned off individually. Visuals only; gameplay and saves are not touched.
- **Ping meter**, in-lobby map proposals and chat map picker, Workshop map import, diagnostics shared only with Steam friends.
- **Signed self-update** between friends and through the Steam Workshop (RSA-signed packages; the public key is in `UpdateKey.cs`).
- Optional **hitch profiler** (`[Diag] HitchProfiler`) that shows which game components allocate memory and cause GC pauses.

### Install

1. Subscribe to the [Workshop item](https://steamcommunity.com/sharedfiles/filedetails/?id=3818506669) (or download the installer from Releases), close the game.
2. Run `CastleStory-Steam-Setup.exe` once and choose the original Castle Story folder. BepInEx 5 and the plugin are installed with backups.
3. Start the game **through Steam**. Press **F8** for the bridge window.

Both players need the mod. A player without it cannot join a modded lobby over the internet.

### Build

Requirements: Windows, the .NET Framework 4.x C# compiler (`csc.exe`), a Castle Story installation with BepInEx 5 (for reference assemblies).

```powershell
cd Source
.\BuildPlugin.ps1 -GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"   # plugin only
.\Release.ps1     -GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"   # signed release + installer + self-test
```

`Release.ps1` needs your own RSA signing key (`New-SigningKey.ps1` creates `%USERPROFILE%\.castlestory-signing\private-key.xml`; **never commit it**). The language is C# 5 (Mono 2.0): no `?.`, no `$""`.

### How it works (short)

The game talks UNET over UDP on localhost. The host side of the bridge listens as the game's server; each guest's bridge pretends to be the server on a local port and forwards the datagrams over `SteamNetworking` (channel 77, reliable by default). Position updates of units additionally go on channel 78 (unreliable) and are written into `UNetTransform.LastServerPosition` on the guest. Harmony patches add the moderation, filters and the timeout policy.

### Status and known limits

- Tested with two accounts on one PC (Helix, Invasion) under artificial delay, packet loss and host stalls; **not yet widely tested over real internet routes**.
- Full garbage-collection pauses of the game (~0.8 s every 15-55 s with two players) still happen; the position channel hides them only when the main stream, not the whole host, is delayed.
- Windows may warn about an "unknown publisher": the installer has no paid code-signing certificate. Updates delivered through Steam are verified by the mod's own RSA signature.

### Credits

Author: **gnom**. Developed with OpenAI Codex and Claude (Anthropic). Uses BepInEx and HarmonyX.

---

## Українською

**Castle Story Steam Bridge** — плагін BepInEx 5 для оригінальної *Castle Story* (Steam), який дає змогу заходити до одне одного через інтернет за допомогою **Steam P2P**: без IP-адрес, проброшення портів і VPN. Він тунелює мережевий трафік гри (UNET) через Steam і додає стабільність та зручності.

> Неофіційний безплатний мод спільноти, не пов'язаний із Sauropod Studio. Потрібна власна копія Castle Story у Steam. У репозиторії **немає файлів гри й декомпільованого коду гри**.

### Можливості

- **Гра через інтернет по Steam P2P**: друзі й сторонні (публічні лобі з'являються в «Публічному списку серверів» з позначкою `[Мод]`; запасний шлях — код хоста). Якщо прямий шлях неможливий, Steam пускає трафік через свій relay.
- **Модерація хоста**: Кік, Бан 10 хв, Бан 60 хв, список заблокованих зі «Зняти».
- **Окремий канал позицій юнітів (0.3.52+)**: хост шле позиції окремим ненадійним Steam-каналом (10 Гц), тож затримка чи втрата пакета в основному потоці більше не «заморожує» персонажів (анімація йде, а координати стоять). Тест на двох гравцях: при втраті 10% пакетів різких стрибків 35 → 1; під час блокування основного потоку на 3 с персонажі не завмирають. Потрібен мод в обох; інакше працює звичайна синхронізація гри.
- **Тайм-аут UNET для кожного з'єднання**: для хоста й мосту довгий, для прямих підключень — значення гри, тож зависання на кілька секунд не викидають гравців.
- **Фільтр предметів і класів (0.3.53)**: хост вимикає будівлі, блоки та класи бріктронів у вікні з картинками; список передається гостям.
- **Перемикачі візуальних ефектів**: костюми й місяць Геловіну, сніг і шапки зими вимикаються окремо. Лише візуал: геймплей і збереження не змінюються.
- **Пінг**, пропозиції карт у лобі та вибір карти в чаті, імпорт Workshop-карт, діагностика лише для друзів.
- **Підписане самооновлення** між друзями й через Steam Workshop (RSA-підпис; публічний ключ — `UpdateKey.cs`).
- Необов'язковий **профайлер зависань** (`[Diag] HitchProfiler`): які компоненти гри виділяють пам'ять і викликають паузи збирача сміття.

### Встановлення

1. Підпишіться на [елемент Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3818506669) (або візьміть встановлювач із Releases), закрийте гру.
2. Один раз запустіть `CastleStory-Steam-Setup.exe` й оберіть папку оригінальної Castle Story. BepInEx 5 і плагін встановляться з резервними копіями.
3. Запускайте гру **через Steam**. Клавіша **F8** відкриває вікно мосту.

Мод потрібен усім гравцям. Без нього до лобі з модом через інтернет зайти неможливо.

### Збирання

Потрібні: Windows, компілятор C# .NET Framework 4.x (`csc.exe`), встановлена Castle Story з BepInEx 5 (еталонні збірки).

```powershell
cd Source
.\BuildPlugin.ps1 -GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"   # лише плагін
.\Release.ps1     -GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"   # підписаний реліз + встановлювач + самотест
```

Для `Release.ps1` потрібен ваш власний RSA-ключ підпису (`New-SigningKey.ps1` створює `%USERPROFILE%\.castlestory-signing\private-key.xml`; **ніколи не додавайте його до репозиторію**). Мова — C# 5 (Mono 2.0): без `?.` і `$""`.

### Стан і обмеження

- Перевірено двома акаунтами на одному ПК (Helix, Вторгнення) за штучної затримки, втрати пакетів і зависань хоста; **реальні інтернет-маршрути ще мало перевірені**.
- Повні паузи збирача сміття гри (~0,8 с кожні 15–55 с у двох гравців) лишаються; канал позицій приховує їх лише коли затримується основний потік, а не весь хост.
- Windows може попередити про «невідомого видавця»: у встановлювача немає платного підпису коду. Оновлення через Steam перевіряються власним RSA-підписом мода.

### Автори

Автор: **gnom**. Створено з OpenAI Codex і Claude (Anthropic). Використовує BepInEx і HarmonyX.
