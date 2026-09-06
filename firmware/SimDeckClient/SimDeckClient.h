// SimDeckClient.h - ESP32 side of the SimDeck protocol.
//
// Reusable across every cockpit module. A module declares the logical values
// it wants, calls begin(), then poll() in loop(). Values arrive in slot
// order; slot i is the i-th name you subscribed with.
//
// Also handles over-the-air firmware updates pushed by the hub.
//
// IMPORTANT: choose a partition scheme with two app partitions (any
// "with OTA" or "Minimal SPIFFS" scheme). The "Huge APP" schemes have a
// single app partition and OTA will fail at Update.begin() every time.
//
// Keep in sync with simdeck/protocol.py

#pragma once

#include <Arduino.h>
#include <WiFiUdp.h>

#define SIMDECK_PROTO_VERSION 1
#define SIMDECK_CTRL_PORT     27500
#define SIMDECK_MODULE_PORT   27501
#define SIMDECK_DATA_MAGIC    0x5A
#define SIMDECK_MAX_SLOTS     16

// magic, version, seq(2), count, flags, reserved(2).
// Eight, not six: the two reserved bytes 4-byte align the float payload and
// make the header length an explicit constant instead of something each
// implementation derives and gets wrong. An earlier build had the firmware
// assuming 7 while the hub packed 6, so every frame was misread.
#define SIMDECK_HEADER_LEN    8

#define SIMDECK_FLAG_SIM_OK      0x01
#define SIMDECK_FLAG_PROFILE_OK  0x02

typedef void (*SimDeckIdentifyCb)();

class SimDeckClient {
 public:
  // moduleId  - stable and unique, e.g. "accu-01". Identifies this board.
  // moduleType- matches the firmware manifest key, e.g. "accu_panel".
  //             Every board of the same type gets the same firmware.
  // fwVersion - "1.2.0". The hub only pushes something strictly newer.
  void begin(const char* moduleId,
             const char* moduleType,
             const char* displayName,
             const char* fwVersion,
             const char* const* names,
             uint8_t count,
             float rateHz = 30.0f);

  // Call every loop(). Handles hello/retry, ping, frames and OTA offers.
  void poll();

  // Latest value for a slot. NAN if never received.
  float value(uint8_t slot) const;

  // Hub reachable AND the sim is feeding it real data.
  bool live() const;

  bool linked() const { return _linked; }
  uint8_t flags() const { return _flags; }

  // True while an OTA download is in progress - pause animation, the
  // needles will not be updating anyway.
  bool updating() const { return _updating; }

  // Called when the hub's Identify button is pressed.
  void onIdentify(SimDeckIdentifyCb cb) { _identify = cb; }

  // Report an input change (button, encoder detent, switch).
  void sendEvent(const char* inputId, float value);

 private:
  void sendHello();
  void sendPing();
  void sendCtrl(const String& json);
  void handleJson(const char* text);
  void handleFrame(const uint8_t* buf, int len);
  void runOta(const String& url, const String& sha, uint32_t size);
  void otaStatus(const char* state, int pct, const char* err);

  WiFiUDP _udp;
  const char* _id = nullptr;
  const char* _type = nullptr;
  const char* _name = nullptr;
  const char* _fw = nullptr;
  const char* const* _names = nullptr;
  uint8_t _count = 0;
  float _rate = 30.0f;

  float _values[SIMDECK_MAX_SLOTS];
  bool _linked = false;
  bool _updating = false;
  uint8_t _flags = 0;

  IPAddress _hubIp;
  bool _haveHub = false;

  uint32_t _lastHello = 0;
  uint32_t _lastPing = 0;
  uint32_t _lastFrame = 0;
  uint16_t _lastSeq = 0;

  SimDeckIdentifyCb _identify = nullptr;
};
