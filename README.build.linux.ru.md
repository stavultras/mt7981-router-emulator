# Сборка под Linux

[English](README.build.linux.md) · **Русский** · [Обзор](README.ru.md) · [Сборка под Windows](README.build.windows.ru.md)

Проверено на Debian 13, Ubuntu 24.04 / 26.04, Fedora и Arch Linux (x86-64).
Сама сборка root не требует; пакеты нужны один раз.

## 1. Зависимости

Сборка (QEMU): компилятор C, git, meson, ninja, pkg-config, Python 3 с venv
и dev-файлы glib, pixman и zlib. libslirp (сеть user-mode) и libfdt берутся
из системы, если установлены; иначе `build.sh` скачивает и собирает копии,
которые идут с QEMU (нужен доступ к gitlab.freedesktop.org и gitlab.com), так
что без root сборка работает на любой машине, где есть компилятор и эти три
библиотеки.

Инструменты (необязательно): `ubinize` (mtd-utils), `sgdisk` (gdisk; образы eMMC) и `wget` — образы NAND
([`tools/prepare-nand.sh`](tools/prepare-nand.sh), [`tools/mknand.py`](tools/mknand.py)),
`socat` — консоль без терминала (`emulator.sh -S`), bridge/iproute/iptables —
сеть хоста ([`tools/host-bridge.sh`](tools/host-bridge.sh)), libpcap — только
для необязательного `-netdev pcap` (загружается при запуске).

Debian / Ubuntu:

```bash
sudo apt-get install -y build-essential git ninja-build meson pkg-config \
    python3 python3-venv libglib2.0-dev libpixman-1-dev libslirp-dev \
    libfdt-dev libusb-1.0-0-dev zlib1g-dev \
    mtd-utils gdisk wget u-boot-tools device-tree-compiler socat \
    bridge-utils iproute2 iptables libpcap0.8t64
```

(`libpcap0.8` в выпусках до Debian 13 / Ubuntu 24.04.)

Fedora:

```bash
sudo dnf install -y gcc make git ninja-build meson pkgconf-pkg-config \
    python3 glib2-devel pixman-devel libslirp-devel libfdt-devel \
    libusb1-devel zlib-ng-compat-devel \
    mtd-utils-ubi gdisk wget uboot-tools dtc socat \
    iproute iptables-nft libpcap
```

Arch Linux:

```bash
sudo pacman -S --needed base-devel git ninja meson python glib2 pixman \
    libslirp dtc libusb zlib \
    mtd-utils gptfdisk wget uboot-tools socat iproute2 iptables libpcap
```

`tools/host-bridge.sh setup` делает мост постоянным через
`/etc/network/interfaces` (ifupdown в Debian); с NetworkManager или
systemd-networkd создайте `br0` их средствами и используйте только
`host-bridge.sh taps`. Без моста режимы `-w user|offline -l user` вообще не
требуют root.

## 2. Сборка QEMU с машиной mt7981-router

```bash
./build.sh
```

Что делает [`build.sh`](build.sh):

1. клонирует QEMU **v10.1.0** в `src/qemu` (shallow);
2. создаёт ветку `mt7981` и применяет [`qemu-patches/*.patch`](qemu-patches/) через `git am`;
   (патчи, добавленные в репозиторий позже, применяются и к уже скачанному QEMU);
3. конфигурирует `--target-list=aarch64-softmmu --enable-slirp --enable-fdt=enabled`
   (libslirp/libfdt из системы или встроенные);
4. собирает `qemu-system-aarch64` через `ninja` (`JOBS=N` ограничивает число
   заданий, `CONFIGURE_ARGS` добавляет параметры configure).

Результат: `src/qemu/build/qemu-system-aarch64`. Проверка:

```bash
src/qemu/build/qemu-system-aarch64 -M help | grep mt7981
src/qemu/build/qemu-system-aarch64 -M mt7981-router,help    # параметры платы
```

То же вручную:

```bash
git clone --depth 1 --branch v10.1.0 https://gitlab.com/qemu-project/qemu.git src/qemu
cd src/qemu && git checkout -b mt7981 && git am ../../qemu-patches/*.patch
mkdir build && cd build
../configure --target-list=aarch64-softmmu --enable-slirp --enable-fdt=enabled --disable-docs
ninja qemu-system-aarch64
```

После правок в `src/qemu/hw/arm/mt7981/` достаточно запустить `ninja` в
`src/qemu/build`. Обновить серию патчей:
`cd src/qemu && git commit ... && git format-patch -o ../../qemu-patches v10.1.0..mt7981`.

## 3. Образы флеш-памяти

```bash
tools/prepare-nand.sh cudy_wr3000p-v1 25.12.5        # -> nand-wr3000p/
tools/prepare-nand.sh cudy_tr3000-v1 25.12.5         # -> nand-tr3000/
tools/prepare-nand.sh cudy_wr3000p-v1 snapshot       # снапшот вместо релиза
# стоковый загрузчик (дампы BL2 = *mtd0*.bin, FIP = *mtd4*.bin в wr3000u/)
tools/prepare-nand.sh --stock wr3000u --flash-mb 256 cudy_wr3000u-v1 25.12.5
# своя сборка OpenWrt (образы *-ubootmod-* в папке)
tools/prepare-nand.sh --local m3000/<сборка> cudy_m3000-v1 25.12.5
# платы без раздела bdinfo (FIP 0x380000, ubi 0x580000)
tools/prepare-nand.sh --no-bdinfo netis_nx31 25.12.5
# плата с SPI-NOR: стоковые BL2/FIP в wr3000/ + OpenWrt sysupgrade.bin
tools/prepare-nand.sh --stock wr3000 --nor cudy_wr3000-v1 25.12.5
```

ПРОФИЛЬ — имя профиля устройства в OpenWrt. Папка результата — `nand-ИМЯ`
(профиль без префикса производителя и `-v1`), как в `nand-dir` пресетов.

Заранее положите в `factory/` дампы своего роутера `*Factory*.bin`
(калибровка Wi-Fi) и `*bdinfo*.bin` (MAC-адрес); без них Wi-Fi использует
значения по умолчанию, а MAC генерируется случайно.

## Пакет для Linux

```bash
tools/package-linux.sh        # -> dist/Router-Emulator-<версия>-linux-x86_64.tar.gz
```

[`tools/package-linux.sh`](tools/package-linux.sh) (нужен `patchelf`)
упаковывает `qemu/qemu-system-aarch64` с нужными ему разделяемыми
библиотеками (всё, кроме glibc, ищется через `RUNPATH=$ORIGIN`),
`emulator.sh`, пресеты, инструменты и документацию; папок флеша нет.
`emulator.sh` и `tests/quick.py` берут `qemu/qemu-system-aarch64`, если нет
`src/qemu/build`. Пакет работает на дистрибутивах с той же или более новой
glibc, чем на машине сборки. Чтобы зависимостей было меньше, соберите QEMU
без интерфейса и звука:

```bash
CONFIGURE_ARGS="--disable-gtk --disable-sdl --disable-opengl --disable-vnc \
  --disable-spice --disable-curl --audio-drv-list=" ./build.sh
```

Workflow GitHub [`.github/workflows/build.yml`](.github/workflows/build.yml)
собирает этот пакет и zip для Windows на каждый push, загружает OpenWrt в
обоих (Windows — на Windows-раннере) и прикладывает их к GitHub Release при
пуше тега.

## 4. Сеть хоста (необязательно)

```bash
sudo tools/host-bridge.sh setup enp0s3 br0   # сетевая карта в br0 (IP/MAC сохраняются, постоянно)
sudo tools/host-bridge.sh taps br0 $USER     # wr-wan -> br0, wr-lan1..4 -> изолированный br-wrlan
tools/host-bridge.sh status
sudo tools/host-bridge.sh teardown           # откат
```

В виртуальной машине (например, VirtualBox) включите для адаптера режим
promiscuous «Allow All», иначе кадры для MAC-адресов роутера не доходят.
`setup` сохраняет резервную копию `/etc/network/interfaces`.

## 5. Запуск

```bash
./emulator.sh                      # пресет cudy-wr3000p-v1, WAN в br0, lan1 в br-wrlan
./emulator.sh -P list              # пресеты (presets/*.ini)
./emulator.sh -P cudy-tr3000-v1 -w user -l none
./emulator.sh -P cudy-wr3000p-v1 -o usb-port=3,ram=1024   # изменить железо
./emulator.sh -h                   # все параметры
```

Консоль — этот терминал (Ctrl-A X — выход, Ctrl-A C — монитор QEMU). Логи:
`logs/console_*.log`. Быстрые автоматические проверки: [`tests/quick.py`](tests/quick.py).

Без root и без терминала (скрипты, CI): WAN через сеть QEMU user-mode,
отрезанную от интернета (`-w offline`), LAN1 проброшен на 127.0.0.1
(`-l user`, порты из пресета, по умолчанию 8080 → 80, 8443 → 443,
8022 → 22), консоль на unix-сокете:

```bash
./emulator.sh -P cudy-wr3000p-v1 -w offline -l user -S work/console.sock &
socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:work/console.sock   # Ctrl-] — отключиться
curl -s http://127.0.0.1:8080/ | head                 # LuCI
echo quit | socat - UNIX-CONNECT:work/monitor.sock    # остановить
```
