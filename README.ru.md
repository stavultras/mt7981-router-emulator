# Router Emulator (MediaTek MT7981B / Filogic 820, MT7986 / Filogic 830, MT7987)

[English](README.md) · **Русский** · Сборка: [Linux](README.build.linux.ru.md) · [Windows](README.build.windows.ru.md)

QEMU-машина `mt7981-router`, эмулирующая плату роутера на MT7981B на
уровне железа. Железо платы (PHY/коммутатор Ethernet, флеш, тип и размер
RAM, USB) настраивается, поэтому одна машина покрывает много устройств;
конкретные роутеры описываются **пресетами** (`presets/*.ini`). Прошивки
запускаются **без изменений**, по той же цепочке загрузки, что и на
настоящем устройстве:

```
BootROM (эмулирован) → BL2 (preloader MediaTek, калибровка DDR) → BL31 (TF-A)
→ U-Boot → OpenWrt (ядро + rootfs из UBI на SPI-NAND)
```

Загружаются и собственный загрузчик OpenWrt (разметка «ubootmod»), и
стоковые загрузчики производителя (BL2/FIP, снятые с настоящих устройств,
с NMBM). Всё, что для этого понадобилось, сделано на стороне эмулятора;
прошивки не патчатся.

Платы на MT7986A/B (Filogic 830) работают на второй машине,
`mt7986-router` (ключ пресета `soc=mt7986`): те же периферийные блоки,
четыре ядра, BL2 для MT7986 со своей калибровкой DRAM и определением её
размера, см. [MT7986](#mt7986-filogic-830); платы на MT7987A/B — на
`mt7987-router` (`soc=mt7987`, см. [MT7987](#mt7987)).

## Быстрый старт

Linux: [README.build.linux.ru.md](README.build.linux.ru.md) (или
`Router-Emulator-<версия>-linux-x86_64.tar.gz` из релиза: QEMU со
своими библиотеками, собирать не нужно), затем

```bash
tools/prepare-nand.sh cudy_wr3000p-v1 25.12.5   # официальные образы -> nand-wr3000p/
./emulator.sh -P list                             # пресеты плат
./emulator.sh -P cudy-wr3000p-v1                  # консоль роутера в этом терминале
```

Windows: скачайте zip из релиза (или соберите сами —
[README.build.windows.ru.md](README.build.windows.ru.md)), распакуйте,
запустите `emulator.exe`. Папок NAND в релизе нет: соберите их
`tools/prepare-nand.sh` (Linux или WSL) и положите рядом с `emulator.exe`
или используйте дампы с настоящего роутера.

## Железо платы (параметры машины)

`-M mt7981-router,nand-dir=ПАПКА,<параметры>` (или `mt7986-router`) и
`-m <размер RAM>`:

| Параметр | Значения | Смысл |
|---|---|---|
| `gmac0` | `mt7531` · `rtl8221b` · `yt8821` · `gpy211` · `none` | что подключено к GMAC0 (mac@0, SGMII0) |
| `ports` | например `wan:lan1:lan2:lan3:lan4` | id netdev портов 0..4 MT7531 и порта 5 при `port5=` (`-` — не используется) |
| `port5`, `port5-phy-addr`, `port5-reset-gpio` | `rtl8221b` · `yt8821` · `gpy211` · `none`; адрес MDIO (5); GPIO | 2.5G PHY на порту 5 MT7531 (SGMII), например Netcore N60 Pro; его netdev — шестой элемент `ports` |
| `gmac0-port`, `gmac1-port` | id netdev | порт PHY, подключённого прямо к GMAC |
| `gmac1` | `rtl8221b` · `yt8821` · `gpy211` · `gphy` · `i2p5ge` · `none` | GMAC1 (mac@1); `gphy` — встроенный 1G PHY MT7981; `i2p5ge` — внутренний 2.5G PHY MT7987; `gpy211` — MaxLinear GPY211C |
| `gmac0-reset-gpio`, `gmac1-reset-gpio` | GPIO, `-1` (по умолчанию) | линия аппаратного сброса 2.5G PHY; по умолчанию не подключена (отсутствие линии безвредно, неверная могла бы держать PHY в сбросе) |
| `gmac0-phy-addr`, `gmac1-phy-addr` | адрес MDIO | адрес этого PHY (по умолчанию 1 на gmac0, 6 на gmac1) |
| `flash` | `nand` · `nor` · `emmc` | загрузочный флеш: SPI-NAND на SPI0 (по умолчанию), SPI-NOR на SPI2 (MT7986: SPI0) или eMMC (см. ниже) |
| `nand` | `128` · `256` | SPI-NAND W25N01GV / W25N02KV |
| `nand-spi` | `0` (по умолчанию) · `1` · `2` | контроллер SPI, на котором SPI-NAND (BPI-R4 Lite: 2) |
| `switch-irq-gpio` | GPIO | EINT прерывания MT7531 (по умолчанию 38 / 66 / 41 для MT7981 / MT7986 / MT7987) |
| `flash=emmc`, `emmc-boot` | МБ (4) | вместо этого eMMC на MSDC0: один образ (первый `*.img` в папке флеша или `-drive if=sd`) = boot0 + boot1 (по `emmc-boot` МБ) + пользовательская область, размер — степень двойки |
| `nor`, `nor-id` | МБ (16), JEDEC ID (`ef4018`) | размер и ID SPI-NOR, например `204018` = XMC XM25QH128C, `c84018` = GD25Q128; `nor=64,nor-id=ef4020` = Winbond W25Q512JV (64 МБ, 4-байтовая адресация; например, WR3000 с перепаянной микросхемой) |
| `ddr` | `ddr3` · `ddr4` | тип распаянной памяти: BL2, собранный для другого типа, останавливает машину, как не проходит инициализация DRAM на настоящей плате |
| `usb-port` | `none` · `2` · `3` | разъём USB (USB 3.0: устройства подключаются на SuperSpeed) |
| `usb-host` | `VID:PID[;VID:PID…]` | проброс USB-устройств этого ПК в роутер (корневые порты USB 2.0 №2, 3, …, в том числе на платах без разъёма), например Wi-Fi-донгла, LTE-модема, USB-Ethernet; см. «USB Wi-Fi-донгл» |
| `pcie-wifi` | `none` · `mt7992` | только MT7987: карта в слоте PCIe. `mt7992` — MediaTek MT7992, Wi-Fi 7 (2,4 + 5 ГГц); OpenWrt работает с ней через `kmod-mt7996e` и прошивки `kmod-mt7992-firmware`, которых нет в официальных образах MT7987 |
| `reset-gpio`, `wps-gpio` | GPIO | кнопки (QOM `/machine/pinctrl` `reset-button`, `wps-button`) |
| `reset-active-high`, `wps-active-high` | `on` | кнопка читается как 1 при нажатии (по умолчанию active low) |
| `reset-hold` | мс | включение с зажатым reset (TFTP recovery в U-Boot) |
| `poweroff` | `stop` · `reboot` | `stop` (по умолчанию): эмулятор завершается, когда Linux выключается («reboot: Power down» в UART0); `reboot`: как настоящая плата, прошивка которой не умеет выключаться |
| `gpio-log` | `on` | печать изменений выходов GPIO и состояний светодиодов |
| `led1` … `led32` | `ИМЯ: ЦВЕТ ИСТОЧНИК [+ ЦВЕТ ИСТОЧНИК…]` | светодиоды передней панели (см. ниже); `led-state` (QOM `/machine`) отдаёт их состояние |
| `efuse` | файл | загрузить содержимое eFuse из дампа настоящей платы (до 4 КБ, как читается из `/sys/bus/nvmem/devices/nvmem0/nvmem`), чтобы калибровки и данные чипа совпадали с этой платой |
| `efuse-uid` | 32 hex-цифры | задать уникальный блок чипа, чтобы несколько эмулированных плат не были одинаковыми |
| `nand-uid` | 32 hex-цифры | уникальный ID SPI-NAND (по умолчанию выводится из имени папки флеша); стоковая прошивка Cudy проверяет уникальный ID NAND, без него вход в веб-интерфейс закрыт |

Светодиоды передней панели: `led1=Status: red gpio:11:low + white gpio:10:low` —
одна лампа двух цветов, у каждого цвета свой GPIO (`:low` = GPIO_ACTIVE_LOW).
Источники: `gpio:N[:low]` (GPIO SoC), `phy:ADDR:N` (вывод LED N у Ethernet
PHY с адресом MDIO ADDR, как его запрограммировали регистры LED: RTL8221B,
GPY211; например, лампа WAN у WR3000P в актуальном OpenWrt), `ws2812:BUS:N`
(цепочка RGB WS2812B на шине SPI BUS, цвет `rgb`: Redmi AX6000) и `pwm:N`
(канал PWM, pwm-leds: BPi-R4 Lite). QEMU опрашивает их каждые 50 мс;
`qom-get /machine led-state` выдаёт по каждой лампе (`;`) и цвету (`,`)
`0`, `1`, `b` (мигает) или `#rrggbb` / `b#rrggbb`. Windows-лаунчер
показывает их рядом лампочек над консолью; с `-g` (`gpio-log=on`) QEMU
печатает каждое изменение («LED Status: white on»).
[`tools/dts-leds.py`](tools/dts-leds.py) заполняет ключи `led` пресетов из
device tree OpenWrt (`--write --all`).

Сетевые порты — netdev QEMU с id из таблицы (`wan`, `lan1`, …).
Ключи пресета только для лаунчеров: `lan-ip` (адрес роутера в LAN, по
умолчанию 192.168.1.1) и `lan-forwards` (по умолчанию
`8080:80,8443:443,8022:22`, порт ПК:порт роутера) для порта LAN «только этот
ПК»: сеть QEMU user-mode в /24 роутера с `restrict=on` и этими пробросами
со 127.0.0.1. Собственный DHCP-сервер QEMU в ней выключен (`dhcp=off`,
нужен libslirp ≥ 4.7): dnsmasq OpenWrt не раздаёт адреса на br-lan, пока
отвечает другой сервер.

## Пресеты плат

Пресет — INI-файл: `name`, `description`, `soc` (`mt7981` по умолчанию,
`mt7986`), `ram` (МБ), `nand-dir`, ключи
только для сборки пакета (`openwrt=` профиль OpenWrt, `openwrt-local=`
своя сборка, `openwrt-stock=` дампы стокового загрузчика) и параметры
машины. Их используют оба лаунчера; в Windows-лаунчере есть редактор
(Ethernet, подписи портов коммутатора, тип/размер RAM, NAND, USB, GPIO
кнопок с галкой «active high» — снята = active low, это умолчание и так
у большинства роутеров; при сохранении из редактора комментарии `;`
пропадают).

| Пресет | Ethernet | RAM | NAND | USB | Загрузчик |
|---|---|---|---|---|---|
| Cudy WR3000P v1 | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR4 512 МБ | 128 МБ | 2.0 | OpenWrt |
| Cudy WR3000H v1 | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR3 512 МБ | 128 МБ | – | OpenWrt |
| Cudy WR3000S v1, WR3000E v1 | 5×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | 128 МБ | – | OpenWrt |
| Cudy WBR3000UAX v1 | 5×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | 128 МБ | 3.0 | OpenWrt |
| Cudy WR3000U v1 | 5×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | 256 МБ | – | стоковый (NMBM) |
| Cudy TR3000 v1 | 2.5G WAN RTL8221B на GMAC0 + 1G LAN встроенный PHY | DDR3 512 МБ | 128 МБ | 3.0 | OpenWrt |
| Cudy TR3000 256MB v1 | то же, без коммутатора | DDR3 512 МБ | 256 МБ | 3.0 | стоковый (NMBM) |
| Cudy M3000 v1 / v2 (RTL8221B) | 2.5G WAN RTL8221B + 1G LAN встроенный PHY | DDR3 256 МБ | 128 МБ | – | OpenWrt (своя сборка) |
| Cudy M3000 v2 (YT8821) | 2.5G WAN Motorcomm YT8821 + 1G LAN встроенный PHY | DDR3 256 МБ | 128 МБ | – | OpenWrt (своя сборка) |
| Netis NX30 V2, NX31 | 1G WAN встроенный PHY + 3×1G LAN MT7531 | DDR3 256 МБ | 128 МБ | – | OpenWrt |
| Netis NX32U | 4×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | 128 МБ | 3.0 | OpenWrt |
| Cudy WR3000 v1 | 4×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | **SPI-NOR 16 МБ** (XM25QH128C) | – | стоковый |
| Xiaomi Mi Router AX3000T | 4×1G MT7531 (WAN = порт 0) | DDR3 256 МБ | 128 МБ, разметка Xiaomi | – | OpenWrt |
| Huasifei WH3000 Pro NAND | 2.5G LAN RTL8221B + 1G WAN встроенный PHY, без коммутатора | DDR4 1 ГБ | 256 МБ, без bdinfo | 3.0 | стоковый (NMBM) |
| Huasifei WH3000R NAND | 1G WAN встроенный PHY + 3×1G LAN MT7531 | DDR3 512 МБ | 256 МБ, без bdinfo | 3.0 | стоковый (NMBM) |
| Keenetic KN-1012 | 1G WAN встроенный PHY + 4×1G LAN MT7531 (SFP не эмулируется) | DDR4 512 МБ | 256 МБ | 3.0 | стоковый (NMBM) |
| Xiaomi Redmi AX6000 (MT7986A) | 4×1G MT7531 (WAN = порт 4) | DDR4 512 МБ | 128 МБ | – | OpenWrt |
| Netcore N60 (MT7986A) | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR3 256 МБ | 128 МБ | – | OpenWrt |
| Netcore N60 Pro (MT7986A) | 2.5G WAN GPY211 + 2.5G LAN GPY211 на порту 5 коммутатора + 3×1G MT7531 | DDR4 512 МБ | 128 МБ | 3.0 | OpenWrt |
| Bananapi BPi-R4 Lite (MT7987A) | 2.5G WAN внутренний PHY + 4×1G MT7531 (SFP не эмулируется); карта Wi-Fi 7 MT7992 на PCIe — для Wi-Fi поставьте `kmod-mt7996e kmod-mt7992-firmware` | DDR4 2 ГБ | 256 МБ на SPI2, FIP в UBI | 3.0 | OpenWrt |
| GL.iNet GL-MT6000 (MT7986A) | 2.5G WAN RTL8221B + 2.5G LAN RTL8221B на порту 5 коммутатора + 4×1G MT7531 | DDR4 1 ГБ | **eMMC** | 3.0 | OpenWrt |
| Huasifei WH3000 Pro eMMC | 2.5G LAN RTL8221B + 1G WAN на встроенном PHY, без коммутатора | DDR4 1 ГБ | **eMMC**, разметка производителя | 3.0 | OpenWrt (сборка для RAX3000M eMMC DDR4) |

У плат Netis нет раздела bdinfo (FIP с 0x380000, ubi с 0x580000; MAC — в
Factory): в пресетах стоит `openwrt-no-bdinfo=1`. У плат на MT7986 та же
разметка. Другие разметки задаются ключом `openwrt-parts=метка:размер,...`
(`tools/prepare-nand.sh --parts`), например у Xiaomi AX3000T: BL2, Nvram,
Bdata, Factory, FIP, crash, crash_log, ubi с 0x600000, KF. Его прошивка
называет порты LAN lan2..lan4; `ports` в пресете — имена подключений
эмулятора, поэтому LAN лаунчера (`lan1`) — это порт 1 коммутатора.

## MT7986 (Filogic 830)

`mt7986-router` — та же машина с отличиями MT7986: четыре ядра
Cortex-A53, HW_CODE 0x7986, pinctrl по 0x1001f000 (101 GPIO), eFuse по
0x11d00000, два контроллера SPI, нет встроенного GbE PHY, прерывание
MT7531 на EINT 66. Его BL2 (готовая библиотека DRAM от MediaTek, DDR3 и
DDR4) калибруется на той же модели контроллера DRAM и определяет размер
памяти тестовым агентом контроллера (запись за пределами `-m`
заворачивается, как на настоящем чипе). Драйвер Wi-Fi видит один a-die
MT7976 (2,4 + 5 ГГц); EEPROM по умолчанию для MT7986 в OpenWrt нет, поэтому
`tools/prepare-nand.sh --soc mt7986` записывает минимальный в пустой
Factory (с дампом Factory настоящей платы берётся её калибровка).

```bash
tools/prepare-nand.sh --soc mt7986 --no-bdinfo netcore_n60-pro 25.12.5 nand-n60-pro
./emulator.sh -P netcore-n60-pro
```

## MT7987

`mt7987-router`: UART по 0x11000000, SPI0..2 по 0x11007800 / 0x11008800 /
0x11009800, TOP_MISC по 0x10021000, eFuse по 0x11d30000, frame engine
NETSYS v3 (PDMA по 0x6800), внутренний 2.5G PHY (`gmac1=i2p5ge`, MDIO 15;
загрузка прошивки и линк), датчик LVTS, регистр бутстрапа (тип DDR для BL2
«comb», загрузочный носитель — по нему U-Boot выбирает rootdisk). BL2 для
MT7987 калибрует DRAM на той же модели контроллера. Параметры для его
плат: `nand-spi=2` (SPI-NAND на SPI2), `switch-irq-gpio=41`, `-m 2G`.

Wi-Fi у MT7987 на PCIe: в машине есть контроллер Gen3 порта 0
(`mt7987_pcie.c`: линк, TLP конфигурации, INTx, приём MSI; корневой порт
— стандартный из QEMU), а с `pcie-wifi=mt7992` в слоте стоит карта
MT7992, Wi-Fi 7 (`mt7992_wifi.c`, «немая», как встроенный Wi-Fi других
SoC: прошивка загружается и отвечает, оба диапазона поднимаются, hostapd
работает, в эфир ничего не уходит). В официальном образе драйвера нет;
поставьте его один раз (WAN должен видеть интернет, пакеты сохранятся в
overlay):

```bash
apk update && apk add kmod-mt7996e kmod-mt7992-firmware
reboot
```

Затем включите в LuCI `radio0` / `radio1` и их сети (в OpenWrt Wi-Fi по
умолчанию выключен).

```bash
tools/prepare-nand.sh --soc mt7987 --flash-mb 256 --ubi-fip bananapi_bpi-r4-lite 25.12.5
./emulator.sh -P bananapi-bpi-r4-lite
```

Платы с eMMC (GL.iNet GL-MT6000) — `flash=emmc`: модель контроллера MSDC
обслуживает TF-A и U-Boot (PIO) и Linux (DMA по дескрипторам); BL2
загружается из загрузочного раздела boot0 eMMC («EMMC_BOOT»), FIP, ядро и
rootfs — разделы GPT. `tools/prepare-nand.sh --soc mt7986 --emmc
glinet_gl-mt6000 25.12.5` собирает образ через
[`tools/mkemmc.py`](tools/mkemmc.py) (разреженный, 1 ГБ).

У Huasifei WH3000 Pro eMMC (MT7981B) разметка GPT от производителя
(u-boot-env, factory, fip, config, kernel, rootfs; `--emmc-layout
wh3000-pro`), а OpenWrt собирает для неё только прошивку (`sysupgrade.bin` —
tar с FIT ядра и squashfs корня), без загрузчика. Пресет (`openwrt-uboot=`,
`--uboot`) берёт BL2 и FIP OpenWrt от CMCC RAX3000M eMMC DDR4 (тот же SoC,
носитель загрузки и тип памяти) и записывает окружение U-Boot, которое
загружает FIT из раздела `kernel`; `root=PARTLABEL=rootfs` ядро берёт из
своего дерева устройств, как и со стоковым загрузчиком. С дампами стоковых
BL2 (`*boot0*.bin`) и FIP (`*fip*.bin`) ключ `--stock ПАПКА` оставляет
стоковый загрузчик:

```bash
tools/prepare-nand.sh --emmc --emmc-layout wh3000-pro --uboot cmcc_rax3000m-emmc-ddr4 huasifei_wh3000-pro-emmc 25.12.5 emmc-wh3000-pro
./emulator.sh -P huasifei-wh3000-pro-emmc
```

## Что эмулируется

Все модели устройств — в `hw/arm/mt7981/` дерева QEMU
(патчи: [`qemu-patches/`](qemu-patches/), база QEMU v10.1.0).

| Блок | Модель | Примечания |
|---|---|---|
| CPU, GIC | 2× (MT7986: 4×) Cortex-A53 (EL3/EL2), GICv3, таймер 13 МГц | остальные ядра запускает BL31 через SPMC в TOPMISC; PSCI — от BL31 |
| BootROM | высокоуровневая эмуляция (`mt7981_router.c`) | разбирает заголовок `SPINAND!` и GFH `FILE_INFO`, грузит BL2 в L2 SRAM, переходит на EL3 |
| Контроллер DRAM | `mt7981_sysctl.c` + сгенерированная таблица статусов | broadcast, ответы RTSWCMD/MRW, jitter meter, lead/lag DQS gating, «глаз» приёма RX: бинарная калибровка DRAM от MediaTek находит настоящие окна, лог BL2 чистый (DDR3 и DDR4); биты DDR3EN/DDR4EN регистра `DDRCOMMON0` сверяются с `ddr=` |
| Клоки, питание, прочее | `mt7981_sysctl.c` | разреженный регистровый файл + особые случаи (частотомер, включение CPU, TRNG v2, калибровочные данные eFuse, IPPC, термодатчик ≈45 °C, ID EIP-97, биты сброса WED) |
| APXGPT | `mt7981_sysctl.c` | 8 таймеров общего назначения (нужны стоковым BL2) |
| TOPRGU | `mt7981_toprgu.c` | watchdog с реальным таймаутом, программный сброс (reboot), статус сброса сохраняется |
| UART ×3 | 16550 из QEMU + регистры MTK | |
| SPI (IPM) | `mt7981_spim.c` | FIFO + DMA, полудуплексный режим spi-mem (TF-A/U-Boot/Linux) |
| SPI-NAND | `spinand.c` | W25N01GV (2048+64) / W25N02KV (2048+128), on-die ECC, ONFI parameter page, страница уникального ID; хранилище — папка с дампами разделов (см. ниже) или один raw-образ с OOB |
| eMMC | `mt7981_msdc.c` + eMMC из QEMU | контроллер MSDC: команды, авто CMD23/CMD12, PIO через FIFO (TF-A, U-Boot), DMA по дескрипторам GPD/BD (Linux); карта с boot0/boot1 и пользовательской областью; BootROM загружает образы `EMMC_BOOT` из boot0 |
| SPI-NOR | `spinor.c` | 3-байтовая адресация (до 16 МБ), задаваемый JEDEC ID, чтение 1-1-1/1-1-2/1-1-4/1-2-2/1-4-4, запись страниц, стирание 4K/32K/64K/всего чипа, регистры статуса с QE; BootROM загружает с него образы `SF_BOOT`; хранилище — такая же папка |
| Ethernet | `mt7981_eth.c` | frame engine: QDMA TX (Linux), PDMA RX, PDMA v2 (U-Boot); TSO и offload контрольных сумм; 2× LynxI SGMII PCS; любая комбинация коммутатора и PHY на двух GMAC |
| Коммутатор | `mt7981_eth.c` | MT7531: страничный доступ по MDIO, косвенный доступ к PHY, special tag MTK (DSA), FDB с обучением, port matrix, IRQ линка → EINT 38 |
| PHY | `mt7981_eth.c` | 5× GPHY MT7531; RTL8221B-VB-CG (C45, термодатчик); Motorcomm YT8821 (расширенные регистры, пространства UTP/SerDes, статус 2.5G); MaxLinear GPY211C (C45, версия прошивки, mailbox, термодатчик); встроенный GbE PHY MT7981 (с калибровкой); PHY на порту 5 MT7531; необязательные GPIO аппаратного сброса |
| GPIO / EINT | `mt7981_pinctrl.c` | кнопки (reset/WPS, `reset-hold-ms` для нажатия на время), лог светодиодов, уровни выводов для других устройств |
| Светодиоды | `mt7981_leds.c`, `ws2812b.c` | светодиоды передней панели из пресета: GPIO, регистры LED у PHY (RTL8221B, GPY211), WS2812B на SPI, каналы PWM; распознавание мигания; `led-state` |
| USB | xHCI из QEMU + IPPC MTK | разъём USB 2.0 / 3.0 |
| Wi-Fi | `mt7981_wmac.c` | кольца WFDMA + эмуляция командного интерфейса прошивок WM/WA: прошивка грузится, оба диапазона поднимаются, hostapd работает, в эфире ничего нет (сканирование пустое) |
| PCIe (MT7987) | `mt7987_pcie.c` | хост MediaTek Gen3: линк, TLP конфигурации через CFGNUM, INTx, приём MSI; корневой порт из QEMU |
| Карта Wi-Fi 7 (MT7987) | `mt7992_wifi.c` | MT7992 для mt7996e: окна BAR0, кольца WFDMA, загрузка патча и прошивок, ответы на UNI-команды, события TX free; радио «молчит» |
| Крипто (EIP-97) | только ID | драйвер safexcel видит «нет packet engine» и отключается; работает программная криптография |
| WED | только биты сброса | с `wed_enable=1` mt7915e видит, что WO MCU нет, и работает без offload |

Новый netdev `pcap` (`net/pcap.c`, libpcap / Npcap загружается при запуске)
подключает порт к сетевой карте хоста как «сетевой мост» — им пользуется
Windows-лаунчер.

## Флеш (папка NAND)

Папка NAND содержит дампы разделов без OOB. **Каждый файл, в имени которого
есть `mtdN`, становится разделом N**; файлы склеиваются по порядку mtd0,
mtd1, … в полный образ, например `mt7981.mtd0.BL2.bin`,
`mt7981.mtd1.u-boot-env.bin`, `mt7981.mtd2.Factory.bin`,
`mt7981.mtd3.bdinfo.bin`, `mt7981.mtd4.FIP.bin`, `mt7981.mtd5.ubi.bin`.

Всё, что роутер пишет во флеш (настройки, sysupgrade, env U-Boot),
записывается обратно в эти файлы. Дампы с настоящего роутера
(`cat /dev/mtdN > имя.mtdN.метка.bin`) подходят напрямую. При `flash=nor`
папка — это содержимое SPI-NOR (например, BL2, u-boot-env, Factory,
bdinfo, FIP, firmware). Если файлы меньше флеша (например, дамп 128 МБ
запущен с `nand=256`), при первом запуске последний файл дополняется
стёртыми байтами (`0xff`) до размера флеша, чтобы то, что роутер туда
запишет, сохранялось.

- [`tools/prepare-nand.sh`](tools/prepare-nand.sh) `[--stock ПАПКА [--nor] | --local ПАПКА] [--flash-mb N] [--no-bdinfo] ПРОФИЛЬ [ВЕРСИЯ] [ПАПКА_NAND]` — собирает папку NAND для профиля устройства OpenWrt: скачивает официальные образы с U-Boot OpenWrt (`ПРОФИЛЬ-ubootmod-*` или `ПРОФИЛЬ-*`, с проверкой sha256), или берёт свои сборки (`--local`), или оставляет стоковые BL2/FIP (`--stock`) с OpenWrt `sysupgrade.bin` в стоковой разметке; `--no-bdinfo` — для плат без раздела bdinfo, `--nor` — для плат с SPI-NOR (стоковые BL2/FIP + OpenWrt `sysupgrade.bin` в разделе `firmware`), `--soc mt7986` — для плат на MT7986 (файлы разделов `mt7986.mtdN.*`, минимальный EEPROM Wi-Fi в пустом Factory), `--ubi-fip` — для BL2 «spim-nand-ubi» (BL2 с 0, UBI с 0x200000, FIP — том `fip`). Factory/bdinfo берутся из `factory/`.
- [`tools/mknand.py`](tools/mknand.py) — создание и правка образов: `create` (BL2, FIP, Factory, bdinfo, UBI из `.itb` или `sysupgrade.bin`), `write --part fip`, `read`, `split`, `join`, `--flash-mb 256`, `--no-bdinfo`, `nor` (папка SPI-NOR).
- [`tools/mkemmc.py`](tools/mkemmc.py) — образ eMMC (`flash=emmc`): BL2 в boot0, GPT с разделами u-boot-env, factory, fip, kernel, rootfs (разметка GL-MT6000 или `--layout wh3000-pro`), OpenWrt `squashfs-factory.bin` в kernel + rootfs или `--sysupgrade` (tar sysupgrade), `--boot-env` (окружение U-Boot OpenWrt, загружающее раздел `kernel`).

## Запуск

Linux: [`emulator.sh`](emulator.sh) — `-P ПРЕСЕТ` (`-P list`), `-o ПАРАМЕТРЫ`
(переопределить параметры машины, `ram=`), `-n ПАПКА_NAND`,
`-w bridge|user|offline|none` (`offline` — WAN user-mode с `restrict=on`:
DHCP работает, наружу ничего не уходит), `-l isolated|nic|user|none` (`user` — только этот
ПК, пробросы из пресета), `-p "1 3"` (порты LAN),
`-u ПАПКА` (USB-флешка из папки, FAT16), `-L ПАПКА` (логи консоли),
`-g` (лог GPIO и светодиодов), `-R` (включение с зажатым reset на 10 с → TFTP recovery),
`-d` (лог неэмулированных регистров), `-S СОКЕТ` (без терминала: консоль
на unix-сокете, для скриптов и CI; подключиться —
`socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:СОКЕТ`). Переменные окружения `WAN_EXTRA` /
`LAN_EXTRA` дописываются к user-mode netdev, например
`WAN_EXTRA=",guestfwd=tcp:10.0.2.100:80-cmd:nc 127.0.0.1 8000"` даёт роутеру
локальный тестовый сервер даже при `-w offline`. Пример без терминала и без
root (веб-интерфейс роутера — http://127.0.0.1:8080/):

```bash
./emulator.sh -P cudy-wr3000p-v1 -w offline -l user -S work/console.sock &
socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:work/console.sock   # консоль, Ctrl-] — отключиться
echo quit | socat - UNIX-CONNECT:work/monitor.sock    # остановить
```
Сеть хоста: [`tools/host-bridge.sh`](tools/host-bridge.sh) (`br0` с сетевой
картой для WAN, изолированный `br-wrlan` для LAN — LAN в реальной сети
выставил бы туда DHCP/RA роутера).

Windows: `emulator.exe` — пресет платы (с редактором: New / Edit / Save /
Save as / Delete), папка NAND, WAN/LAN (NAT, «только этот ПК» с пробросом
портов на LuCI/SSH или мост на сетевую карту через Npcap), USB-папка,
папка логов, кнопки Reset/WPS, «Power + Reset: 10 s» (TFTP recovery),
выключение по `poweroff`, встроенный терминал (VT100, по умолчанию 80×24
как в PuTTY, размер прилипает к целым символам, выделение = копирование,
правая кнопка = вставка, Ctrl+Shift+R подгоняет размер консоли роутера
под окно), язык интерфейса переключается на лету
([`languages/*.ini`](languages/): English, Русский; новый язык — копия
`en.ini`; [`tools/gen-lang-en.py`](tools/gen-lang-en.py) пересобирает
`en.ini` из исходников, `--check` показывает непереведённые ключи).

## USB Wi-Fi-донгл

Эмулированные радио Wi-Fi ничего не излучают. Для настоящего Wi-Fi можно
пробросить в роутер USB Wi-Fi-донгл этого ПК (`usb-host=VID:PID`, несколько
через `;`; Windows-лаунчер: «USB-устройства» → «Выбрать...»); OpenWrt управляет им своим драйвером
как ещё одним радио (`radio2`, путь `…/usb1/1-2/1-2:1.0`) рядом с
молчащими встроенными и запускает на нём hostapd, как на настоящем железе.

1. Linux: `./emulator.sh … -o usb-host=148f:5370` (QEMU, собранный с
   libusb: `libusb-1.0-0-dev`). QEMU открывает `/dev/bus/usb/…`, нужны
   права на запись (иначе emulator.sh подскажет правило udev); драйвер
   хоста отпускает донгл, пока им пользуется роутер.
   Windows: установите [UsbDk](https://github.com/daynix/UsbDk/releases)
   (донгл остаётся на своём драйвере Windows и забирается только на время
   работы роутера) или поставьте на него драйвер WinUSB через Zadig.
2. В OpenWrt один раз поставьте драйвер донгла (нужен Интернет на WAN),
   например Ralink RT5370 / RT3070: `apk add kmod-rt2800-usb
   rt2800-usb-firmware`; MediaTek MT7601U: `kmod-mt7601u`; MT7612U:
   `kmod-mt76x2u`; Atheros AR9271: `kmod-ath9k-htc`.
3. Network → Wireless: включите `radio2` (диапазон донгла: 2,4 ГГц у
   донгла 2,4 ГГц; двухдиапазонный работает в одном диапазоне за раз —
   выберите там 5 ГГц), задайте SSID и пароль. Встроенные `radio0` /
   `radio1` можно выключить, в эфир они всё равно не выходят.

Проверено с Ralink RT5370 (148f:5370) на Cudy WR3000P с OpenWrt 25.12.5:
точка доступа на канале 6, WPA2, в мосту br-lan; на Linux и на
Windows 11 с UsbDk.

## Структура репозитория

```
emulator.sh                 запуск под Linux
presets/                  пресеты плат (*.ini)
build.sh                  сборка QEMU (Linux)         → README.build.linux.ru.md
build-windows.sh          пакет для Windows (кросс)   → README.build.windows.ru.md
qemu-patches/             патчи поверх QEMU v10.1.0
tools/                    prepare-nand.sh, mknand.py, host-bridge.sh,
                          gen_dramc_table.py (статусы DRAMC по умолчанию)
windows/                  лаунчер, редактор пресетов, терминал (C#),
                          README.txt для пакета
languages/                тексты лаунчера (en.ini, ru.ini)
tests/                    quick.py (проверки по консоли), powercut.py
factory/                  дампы Factory (EEPROM Wi-Fi) и bdinfo (MAC)
wr3000u/, tr3000/, wr3000/ дампы стоковых BL2/FIP (не в git)
m3000/                    свои сборки OpenWrt (не в git)
```

## Заметки для разработчиков

- Исходники, по которым моделировалось железо: дерево Linux из OpenWrt
  (vanilla + патчи OpenWrt), U-Boot, TF-A от mtk-openwrt, mt76, код DRAMC
  MediaTek из coreboot (MT8192/MT8195, то же поколение DRAMC).
- `-d unimp` логирует каждое обращение к регистрам без явной модели —
  самый быстрый способ понять, что опрашивает новая прошивка.
- [`tests/quick.py`](tests/quick.py) запускает QEMU с консолью на сокете и
  ждёт строки в консоли с жёсткими лимитами времени, например
  `tests/quick.py -P cudy-tr3000-v1 --qemu="-netdev user,id=wan" 'Starting kernel@60' 'eth0: Link is Up@120'`.
  [`tests/powercut.py`](tests/powercut.py) выключает/сбрасывает QEMU в
  случайные моменты и проверяет, что BL2 по-прежнему грузит FIP.
- Windows-сборка собирается clang (нативный TLS): эмулируемый TLS в MinGW GCC
  замедлял выполнение гостя примерно на 30 %; кроме того, она оптимизирована
  по профилю (PGO, 5–17 % на настоящей Windows), а лаунчер выводит QEMU из
  режима эффективности Windows 11. По профилю (`perf`) основное время уходит
  на поиск блоков трансляции в TCG, модели устройств почти не видны; -O3/LTO
  ничего не дали. QEMU уже выполняет каждое из двух ядер гостя в своём
  потоке хоста (MTTCG); один поток гостя на несколько ядер не разложить.
- OpenWrt помечает overlay «готовым» только в конце загрузки; перезагрузка
  раньше заставляет fstools его стереть (как и на железе).

## Ограничения

- Радио Wi-Fi «молчит» (клиентов нет, сканирование пустое); командный
  интерфейс MCU отвечает обобщённо. Offload WED не эмулируется.
- Прерывания PHY не генерируются (изменения линка после загрузки видят
  только драйверы, работающие опросом).
- Нет packet engine EIP-97; PCIe только у MT7987 (карта MT7992); PWM/I2C —
  заглушки.
- Скорость: примерно в 2 раза медленнее настоящего SoC 1,3 ГГц на обычном ПК (TCG).

## Релизы

CI (`.github/workflows/build.yml`) на каждый push собирает пакеты для
Linux и Windows и каждым загружает по одному пресету на каждую конфигурацию
железа, на Linux и на настоящей Windows: WR3000P (DDR4, RTL8221B на GMAC1,
коммутатор), WR3000H (BL2 для DDR3), WR3000S (WAN на порту коммутатора),
TR3000 (без коммутатора, один LAN на встроенном PHY), Netis NX31 (разметка
флеша без bdinfo) и платы на MT7986: Redmi AX6000 (DDR4, WAN на порту
коммутатора), Netcore N60 (DDR3, RTL8221B), N60 Pro (GPY211 на GMAC1 и на
порту 5 коммутатора), GL.iNet GL-MT6000 (eMMC), Huasifei WH3000 Pro eMMC
(разметка eMMC производителя) и Bananapi BPi-R4 Lite на
MT7987 (NAND на SPI2, FIP в UBI, внутренний 2.5G PHY). Папки флеша собираются только для этих тестов и в пакеты
не попадают. Чтобы выпустить релиз, измените [`VERSION`](VERSION) и отправьте
в `main`: если тега `v<VERSION>` ещё нет, CI соберёт с PGO, проверит,
создаст тег и релиз на GitHub с `...-linux-x86_64.tar.gz` и `...-win64.zip`
(без папок флеша). Локально `build-windows.sh` дополнительно собирает
`...-dumps.zip` — только собранные папки флеша (`Router-Emulator/nand-*`,
`nor-*`, `emmc-*`), чтобы распаковать поверх пакета.

## Лицензия

GPL-2.0-or-later, как у QEMU (эмулятор — это набор патчей QEMU и
инструменты вокруг него): см. [LICENSE](LICENSE). Образов прошивок в
репозитории нет; у OpenWrt и прошивок производителей свои лицензии.
