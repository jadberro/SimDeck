// accu_panel_185.ino
// A320 accumulator + brake pressure triple indicator.
// SimDeck hardware module for Waveshare ESP32-S3-LCD-1.85 (360x360 round IPS TFT, ST77916 QSPI).
//
// Board in Arduino IDE:
//   Board:              "ESP32S3 Dev Module"
//   USB CDC On Boot:    "Enabled" (CRITICAL: enables Serial output over Type-C)
//   Flash Size:         "16MB (128Mb)"
//   Partition Scheme:   "16M Flash (3MB APP/9.9MB FATFS)" or any scheme with 2 OTA app slots
//   PSRAM:              "OPI PSRAM"
//   Upload Mode:        "UART0 / Hardware CDC"
//
// Display controller:  ST77916 via QSPI (40MHz)
// IO expander:         TCA9554PWR on I2C (SDA=11, SCL=10, Addr=0x20)
// Backlight:           GPIO 5 (LEDC PWM)

#include <Arduino.h>
#include <WiFi.h>
#include <lvgl.h>
#include <math.h>

#include "I2C_Driver.h"
#include "TCA9554PWR.h"
#include "Display_ST77916.h"
#include "SimDeckClient.h"
#include "gauge_geometry.h"

// ---------------------------------------------------------------- identity

#define FW_VERSION  "1.0.0"
#define MODULE_ID   "accu-185-01"    // unique ID per physical board
#define MODULE_TYPE "accu_panel"     // matches SimDeck firmware manifest

// Wi-Fi & Hub configuration (reads from git-ignored wifi_credentials.h if present)
#if __has_include("wifi_credentials.h")
  #include "wifi_credentials.h"
#else
  static const char* WIFI_SSID = "YOUR_WIFI_SSID";
  static const char* WIFI_PASS = "YOUR_WIFI_PASSWORD";
  static const char* HUB_IP    = ""; // Optional: e.g. "192.168.0.208" or leave blank for broadcast
#endif

// Logical telemetry subscriptions matching SimDeck wire protocol v1
static const char* const SUBS[] = {
    "brake.accum_psi",   // slot 0
    "brake.left_psi",    // slot 1
    "brake.right_psi",   // slot 2
};
static const uint8_t SUB_COUNT = 3;

SimDeckClient hub;

// -------------------------------------------------------------- needles

struct Needle {
  lv_obj_t* img;
  lv_obj_t* shadow;
  const lv_img_dsc_t* art;
  const lv_img_dsc_t* shadowArt;
  int16_t artPivotX, artPivotY;   // from gauge_geometry.h
  int16_t pivotX, pivotY;
  float minVal, maxVal;          // in PSI
  const CalPoint* calPoints;     // piecewise calibration points
  int calCount;
  float smoothing;               // per frame smoothing
  float shown;                   // current filtered PSI
};

// Wire carries raw PSI; scale calibration points are in thousands of PSI
#define K 1000.0f

static Needle nAccum = {
    nullptr, nullptr, nullptr, nullptr,
    NEEDLE_ACCU_ART_PIVOT_X, NEEDLE_ACCU_ART_PIVOT_Y,
    (int16_t)roundf(ACCUM_PIVOT_X), (int16_t)roundf(ACCUM_PIVOT_Y),
    ACCUM_VMIN * K, ACCUM_VMAX * K,
    ACCU_POINTS, 2,
    0.12f, 0.0f
};

static Needle nLeft = {
    nullptr, nullptr, nullptr, nullptr,
    NEEDLE_BRAKE_ART_PIVOT_X, NEEDLE_BRAKE_ART_PIVOT_Y,
    (int16_t)roundf(BRAKE_L_PIVOT_X), (int16_t)roundf(BRAKE_L_PIVOT_Y),
    BRAKE_L_VMIN * K, BRAKE_L_VMAX * K,
    BRAKE_L_POINTS, 3,
    0.16f, 0.0f
};

static Needle nRight = {
    nullptr, nullptr, nullptr, nullptr,
    NEEDLE_BRAKE_ART_PIVOT_X, NEEDLE_BRAKE_ART_PIVOT_Y,
    (int16_t)roundf(BRAKE_R_PIVOT_X), (int16_t)roundf(BRAKE_R_PIVOT_Y),
    BRAKE_R_VMIN * K, BRAKE_R_VMAX * K,
    BRAKE_R_POINTS, 3,
    0.16f, 0.0f
};

// LVGL 8 image descriptors
LV_IMG_DECLARE(img_dial_face);
LV_IMG_DECLARE(img_needle_accu);
LV_IMG_DECLARE(img_needle_brake);
LV_IMG_DECLARE(img_needle_accu_shadow);
LV_IMG_DECLARE(img_needle_brake_shadow);

#define USE_SHADOWS 1

static lv_obj_t* faceObj;
static lv_obj_t* wifiAlertLabel = nullptr;
static uint32_t identifyUntil = 0;

// ---------------------------------------------------------------- LVGL display driver

// Dual line draw buffers in internal DMA SRAM (360x40 lines = 28.8KB each)
#define DISP_BUF_LINES 40
#define DISP_BUF_SIZE  (360 * DISP_BUF_LINES)

static lv_color_t* disp_buf1 = nullptr;
static lv_color_t* disp_buf2 = nullptr;
static lv_disp_draw_buf_t draw_buf;
static lv_disp_drv_t disp_drv;

static void my_disp_flush(lv_disp_drv_t *drv, const lv_area_t *area, lv_color_t *color_p) {
  LCD_addWindow(area->x1, area->y1, area->x2, area->y2, (uint16_t *)color_p);
  lv_disp_flush_ready(drv);
}

static void bsp_display_init() {
  Serial.println("[BSP] Initializing I2C bus (SDA=11, SCL=10)...");
  I2C_Init();

  Serial.println("[BSP] Initializing TCA9554PWR IO expander...");
  TCA9554PWR_Init(0x00);   // all pins output mode

  Serial.println("[BSP] Initializing ST77916 QSPI display...");
  LCD_Init();

  Serial.println("[BSP] Initializing backlight PWM on GPIO 5...");
  Backlight_Init();
  Set_Backlight(100);      // 100% brightness

  Serial.println("[BSP] Allocating LVGL draw buffers...");
  disp_buf1 = (lv_color_t*)heap_caps_malloc(DISP_BUF_SIZE * sizeof(lv_color_t), MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (!disp_buf1) {
    disp_buf1 = (lv_color_t*)heap_caps_malloc(DISP_BUF_SIZE * sizeof(lv_color_t), MALLOC_CAP_SPIRAM);
  }
  disp_buf2 = (lv_color_t*)heap_caps_malloc(DISP_BUF_SIZE * sizeof(lv_color_t), MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (!disp_buf2) {
    disp_buf2 = (lv_color_t*)heap_caps_malloc(DISP_BUF_SIZE * sizeof(lv_color_t), MALLOC_CAP_SPIRAM);
  }

  lv_init();
  lv_disp_draw_buf_init(&draw_buf, disp_buf1, disp_buf2, DISP_BUF_SIZE);

  lv_disp_drv_init(&disp_drv);
  disp_drv.hor_res = 360;
  disp_drv.ver_res = 360;
  disp_drv.flush_cb = my_disp_flush;
  disp_drv.draw_buf = &draw_buf;
  lv_disp_drv_register(&disp_drv);

  Serial.println("[BSP] Display & LVGL 8 initialization complete.");
}

// ---------------------------------------------------------------- helpers

static void needleInitShadow(Needle& n, const lv_img_dsc_t* shadowArt) {
#if USE_SHADOWS
  n.shadowArt = shadowArt;
  n.shadow = lv_img_create(lv_scr_act());
  lv_img_set_src(n.shadow, shadowArt);
  lv_img_set_pivot(n.shadow, n.artPivotX, n.artPivotY);
  lv_obj_set_pos(n.shadow,
                 n.pivotX - n.artPivotX + SHADOW_DX,
                 n.pivotY - n.artPivotY + SHADOW_DY);
  lv_img_set_antialias(n.shadow, true);
#endif
}

static void needleInit(Needle& n, const lv_img_dsc_t* art) {
  n.art = art;
  n.img = lv_img_create(lv_scr_act());
  lv_img_set_src(n.img, art);

  lv_img_set_pivot(n.img, n.artPivotX, n.artPivotY);
  lv_obj_set_pos(n.img, n.pivotX - n.artPivotX, n.pivotY - n.artPivotY);
  lv_img_set_antialias(n.img, true);
  n.shown = n.minVal;
}

static void needleUpdate(Needle& n, float target) {
  if (isnan(target)) return;
  if (target < n.minVal) target = n.minVal;
  if (target > n.maxVal) target = n.maxVal;

  // Smoothing filter
  n.shown += (target - n.shown) * n.smoothing;

  // Value in thousands of PSI for piecewise calibration points
  float val_k = n.shown / K;
  float deg = interpolate_gauge_angle(val_k, n.calPoints, n.calCount);

  // Art points UP (-90 deg); LVGL rotates clockwise in 0.1 deg units
  int16_t a = (int16_t)lroundf((deg + 90.0f) * 10.0f);
  lv_img_set_angle(n.img, a);
#if USE_SHADOWS
  if (n.shadow) lv_img_set_angle(n.shadow, a);
#endif
}

static void onIdentify() {
  identifyUntil = millis() + 4000;
}

// ------------------------------------------------------------------- ui

static void buildUi() {
  lv_obj_t* scr = lv_scr_act();
  lv_obj_set_style_bg_color(scr, lv_color_black(), 0);

  // 360x360 dial face artwork
  faceObj = lv_img_create(scr);
  lv_img_set_src(faceObj, &img_dial_face);
  lv_obj_center(faceObj);

  // Shadows first, then needles (so shadows never render on top of pointers)
  needleInitShadow(nAccum, &img_needle_accu_shadow);
  needleInitShadow(nLeft, &img_needle_brake_shadow);
  needleInitShadow(nRight, &img_needle_brake_shadow);

  needleInit(nAccum, &img_needle_accu);
  needleInit(nLeft, &img_needle_brake);
  needleInit(nRight, &img_needle_brake);

  // Wi-Fi alert message (shown only when connection is not okay)
  wifiAlertLabel = lv_label_create(scr);
  lv_label_set_text(wifiAlertLabel, "PLEASE CHECK WIFI CONNECTION");
  lv_obj_set_style_text_color(wifiAlertLabel, lv_color_hex(0xFFC000), 0);
  lv_obj_set_style_text_font(wifiAlertLabel, &lv_font_montserrat_14, 0);
  lv_obj_set_style_text_align(wifiAlertLabel, LV_TEXT_ALIGN_CENTER, 0);
  lv_obj_set_style_bg_color(wifiAlertLabel, lv_color_black(), 0);
  lv_obj_set_style_bg_opa(wifiAlertLabel, LV_OPA_80, 0);
  lv_obj_set_style_pad_hor(wifiAlertLabel, 8, 0);
  lv_obj_set_style_pad_ver(wifiAlertLabel, 4, 0);
  lv_obj_set_style_radius(wifiAlertLabel, 4, 0);
  lv_obj_align(wifiAlertLabel, LV_ALIGN_CENTER, 0, 130);
  lv_obj_add_flag(wifiAlertLabel, LV_OBJ_FLAG_HIDDEN);
}

static void updateWifiAlert() {
  bool ok = (WiFi.status() == WL_CONNECTED);
  if (!ok) {
    lv_obj_clear_flag(wifiAlertLabel, LV_OBJ_FLAG_HIDDEN);
  } else {
    lv_obj_add_flag(wifiAlertLabel, LV_OBJ_FLAG_HIDDEN);
  }
}

// ----------------------------------------------------------------- boot

void setup() {
  Serial.begin(115200);
  delay(100);

  Serial.println("\n============================================");
  Serial.println("  SimDeck A320 Indicator (Waveshare 1.85\")");
  Serial.printf("  Firmware Version: %s\n", FW_VERSION);
  Serial.println("============================================");

  // Hardware bring-up
  bsp_display_init();
  buildUi();

  // Show alert during initial connection attempt
  lv_obj_clear_flag(wifiAlertLabel, LV_OBJ_FLAG_HIDDEN);
  lv_timer_handler();

  // Connect Wi-Fi
  Serial.printf("[WiFi] Connecting to %s", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.begin(WIFI_SSID, WIFI_PASS);

  uint32_t wifiStart = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - wifiStart < 15000) {
    delay(250);
    Serial.print(".");
    lv_timer_handler();
  }

  if (WiFi.status() == WL_CONNECTED) {
    Serial.printf("\n[WiFi] Connected! IP: %s\n", WiFi.localIP().toString().c_str());
    lv_obj_add_flag(wifiAlertLabel, LV_OBJ_FLAG_HIDDEN);
    lv_timer_handler();
  } else {
    Serial.println("\n[WiFi] Connection timed out (check credentials). Continuing offline...");
  }

  // SimDeck hub client
  hub.onIdentify(onIdentify);
  hub.begin(MODULE_ID, MODULE_TYPE, "A320 Accumulator & Brake Pressure 1.85in", FW_VERSION,
            SUBS, SUB_COUNT, 30.0f, HUB_IP);
}

void loop() {
  hub.poll();

  if (!hub.updating()) {
    float accum = hub.value(0);
    float left  = hub.value(1);
    float right = hub.value(2);

    if (!hub.live()) {
      // Sim disconnected or hub unreachable: smoothly return to zero
      accum = left = right = 0.0f;
    }

    needleUpdate(nAccum, accum);
    needleUpdate(nLeft, left);
    needleUpdate(nRight, right);
  }

  // Reconnect Wi-Fi periodically if dropped
  static uint32_t lastWifiCheck = 0;
  if (millis() - lastWifiCheck > 5000) {
    lastWifiCheck = millis();
    if (WiFi.status() != WL_CONNECTED) {
      WiFi.reconnect();
    }
  }

  updateWifiAlert();
  lv_timer_handler();
  delay(5);
}