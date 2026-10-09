# Сборка пакета для Windows

[English](README.build.windows.md) · **Русский** · [Обзор](README.ru.md) · [Сборка под Linux](README.build.linux.ru.md)

Пакет для Windows **собирается на Linux кросс-компиляцией**: QEMU — с
библиотеками MinGW в Docker-контейнере, лаунчер — компилятором C# из Mono.
Результат: `dist/Router-Emulator-<version>-win64.zip` — распаковать в любое место
на Windows 10/11 x64 и запустить `emulator.exe` (.NET Framework 4.8 входит в
Windows). Npcap (<https://npcap.com>) нужен только для моста портов роутера на
сетевую карту.

## 1. Требования (хост с Linux)

```bash
sudo apt-get install -y docker.io mono-mcs mono-devel zip mtd-utils wget python3 git
```

- Docker: собственный образ QEMU для кросс-сборки Fedora MinGW
  (`src/qemu/tests/docker/dockerfiles/fedora-win64-cross.docker`) плюс clang/lld.
- `mono-mcs` / `mono-devel`: компилятор C# и **эталонные сборки .NET
  Framework 4.8** (`/usr/lib/mono/4.8-api`).
- Дерево исходников QEMU из [сборки под Linux](README.build.linux.ru.md)
  (`./build.sh`; `build-windows.sh` запускает его сам, если `src/qemu` нет).

## 2. Сборка

```bash
./build-windows.sh                    # образы NAND с OpenWrt 25.12.5
VERSION=snapshot ./build-windows.sh   # другая версия OpenWrt
```

Что делает [`build-windows.sh`](build-windows.sh):

1. собирает Docker-образы `qemu-win64-cross` (Dockerfile из QEMU) и
   `qemu-win64-clang` (+ clang, lld);
2. конфигурирует QEMU в `src/qemu/build-win-clang` с
   `clang --target=x86_64-w64-windows-gnu --sysroot=<sysroot Fedora MinGW> -fuse-ld=lld`,
   `--extra-ldflags=-L<каталог libgcc>`, `--enable-slirp --enable-fdt=internal`,
   без GTK/SDL/VNC/OpenGL/curl/tools; запускает `ninja`;
3. копирует `qemu-system-aarch64.exe`, `libslirp-0.dll` и все DLL MinGW, от
   которых они зависят (рекурсивно по `objdump -p`), и делает всем
   `strip --strip-all`;
4. компилирует лаунчер (`windows/Launcher.cs`, `windows/Presets.cs`,
   `windows/Lang.cs`, `windows/Terminal.cs`, `windows/Leds.cs`, `windows/UsbDevices.cs` и `work/Version.cs`, сгенерированный из [`VERSION`](VERSION))
   **против эталонных сборок .NET Framework 4.8** — в библиотеках Mono есть
   более новые методы, которые на Windows дали бы `MissingMethodException`;
5. копирует [`presets/`](presets/), [`languages/`](languages/) и для каждого пресета собирает папку NAND
   через [`tools/prepare-nand.sh`](tools/prepare-nand.sh): `openwrt=ПРОФИЛЬ`
   скачивает официальные образы (`openwrt-version=` заменяет `$VERSION`),
   `openwrt-local=ПАПКА` берёт свои сборки, `openwrt-stock=ПАПКА` оставляет
   стоковый загрузчик из дампов в ПАПКЕ, `openwrt-no-bdinfo=1` выбирает
   разметку без bdinfo, `openwrt-nor=1` собирает папку SPI-NOR, `openwrt-emmc=1` —
   образ eMMC (`openwrt-emmc-layout=` — его разметка GPT, `openwrt-uboot=` —
   профиль, чей U-Boot OpenWrt его загружает); пресеты без образов или дампов
   попадают в пакет без папки NAND;
6. пересобирает QEMU с оптимизацией по профилю (PGO,
   [`tools/pgo-windows.sh`](tools/pgo-windows.sh)): собирает профильный
   рантайм clang под Windows из исходников LLVM compiler-rt (в Fedora его
   нет), инструментированный QEMU, гоняет его под Wine на образе WR3000P
   ([`tools/pgo-train.py`](tools/pgo-train.py)) и пересобирает с профилем;
   на настоящей Windows код гостя быстрее на ~5–17 %, сборка дольше
   примерно на 10 минут, `PGO=0 ./build-windows.sh` пропускает этот шаг;
7. упаковывает всё в `dist/Router-Emulator-<version>-win64.zip`.

Лаунчер также выводит QEMU из «режима эффективности» Windows 11 (EcoQoS) и
повышает ему приоритет: у QEMU нет окна, и на гибридных процессорах Intel
Windows иначе уводит его на E-ядра (замерено: до ~50 % медленнее, пока окно
консоли не в фокусе). Скорость гостя измеряется командами из
[`tests/cpubench.sh`](tests/cpubench.sh).

### Почему clang, а не MinGW GCC

MinGW GCC реализует потоко-локальные переменные эмулируемым TLS
(`__emutls_get_address`, около 5000 мест вызова в QEMU). clang с целью
`x86_64-w64-windows-gnu` использует нативный TLS; код гостя выполняется
примерно на 30 % быстрее. Библиотеки те же, из Fedora MinGW, clang'ом
компилируется только сам QEMU. Обычный GCC тоже работает: configure с
`--cross-prefix=x86_64-w64-mingw32-` в образе `qemu-win64-cross`.

## 3. Состав пакета

```
Router-Emulator/
  emulator.exe           лаунчер + редактор пресетов + терминал
  emulator.ini           настройки лаунчера (создаётся при первом запуске)
  presets/             пресеты плат (*.ini)
  languages/           языки интерфейса (*.ini)
  qemu/                qemu-system-aarch64.exe + DLL
  nand-wr3000p/ ...    папки флеш-памяти (по одной на пресет)
  usb/                 отдаётся роутеру как USB-флешка
  logs/                логи консоли (console_ГГГГ-ММ-ДД_ЧЧ-мм-сс.log)
  README.txt           руководство пользователя (windows/README.txt)
```

## 4. Проверка без Windows

Пакет можно запускать под Wine для быстрых проверок (лаунчеру нужен
`wine-mono`):

```bash
WINEPREFIX=$PWD/work/wineprefix tests/quick.py --win \
  -P cudy-wr3000p-v1 -n work/winpkg/Router-Emulator/nand-wr3000p \
  --qemu="-netdev user,id=wan" 'Starting kernel@60' 'wan: Link is Up@150'
```

Ограничения: во встроенном wpcap Wine нет `pcap_getevent`, поэтому мост через
Npcap проверяется только на настоящей Windows; лаунчер под Wine работает на
Mono, а не на .NET Framework.

## 5. Сборка прямо на Windows (не проверялась)

- Лаунчер: достаточно компилятора C#, входящего в .NET Framework (код
  совместим с C# 5):
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /r:System.Management.dll /out:emulator.exe Launcher.cs Presets.cs Lang.cs Terminal.cs Leds.cs UsbDevices.cs Version.cs`
  (`Version.cs` генерирует `build-windows.sh` из [`VERSION`](VERSION): там `RouterEmulator.AppVersion.Text` и версия сборки)
- QEMU: окружение MSYS2 CLANG64 с обычными зависимостями QEMU, затем те же
  параметры `configure`, что выше (без `--cross-prefix`).
