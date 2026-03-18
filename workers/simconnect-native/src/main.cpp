#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cctype>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

namespace
{
volatile std::sig_atomic_t g_running = 1;

constexpr DWORD kDefinitionOwnship = 1;
constexpr DWORD kRequestOwnship = 1;
constexpr DWORD kEventSimStart = 1;
constexpr DWORD kEventSimStop = 2;
constexpr DWORD kObjectIdUser = 0;
constexpr float kSimConnectUnused = 0.0f;
constexpr DWORD kDataRequestChanged = 0x00000001;

enum SimConnectRecvId : DWORD
{
  SIMCONNECT_RECV_ID_NULL = 0,
  SIMCONNECT_RECV_ID_EXCEPTION = 1,
  SIMCONNECT_RECV_ID_OPEN = 2,
  SIMCONNECT_RECV_ID_QUIT = 3,
  SIMCONNECT_RECV_ID_EVENT = 4,
  SIMCONNECT_RECV_ID_SIMOBJECT_DATA = 8,
};

enum SimConnectDataType : DWORD
{
  SIMCONNECT_DATATYPE_INVALID = 0,
  SIMCONNECT_DATATYPE_INT32 = 1,
  SIMCONNECT_DATATYPE_INT64 = 2,
  SIMCONNECT_DATATYPE_FLOAT32 = 3,
  SIMCONNECT_DATATYPE_FLOAT64 = 4,
  SIMCONNECT_DATATYPE_STRING8 = 5,
  SIMCONNECT_DATATYPE_STRING32 = 6,
  SIMCONNECT_DATATYPE_STRING64 = 7,
  SIMCONNECT_DATATYPE_STRING128 = 8,
  SIMCONNECT_DATATYPE_STRING256 = 9,
};

enum SimConnectPeriod : DWORD
{
  SIMCONNECT_PERIOD_NEVER = 0,
  SIMCONNECT_PERIOD_ONCE = 1,
  SIMCONNECT_PERIOD_VISUAL_FRAME = 2,
  SIMCONNECT_PERIOD_SIM_FRAME = 3,
  SIMCONNECT_PERIOD_SECOND = 4,
};

#pragma pack(push, 1)
struct SimConnectRecv
{
  DWORD dwSize;
  DWORD dwVersion;
  DWORD dwId;
};

struct SimConnectRecvOpen
{
  SimConnectRecv recv;
  char szApplicationName[256];
};

struct SimConnectRecvEvent
{
  SimConnectRecv recv;
  DWORD uGroupID;
  DWORD uEventID;
  DWORD dwData;
};

struct SimConnectRecvException
{
  SimConnectRecv recv;
  DWORD dwException;
  DWORD dwSendID;
  DWORD dwIndex;
};

struct SimConnectRecvSimobjectData
{
  SimConnectRecv recv;
  DWORD dwRequestID;
  DWORD dwObjectID;
  DWORD dwDefineID;
  DWORD dwFlags;
  DWORD dwentrynumber;
  DWORD dwoutof;
  DWORD dwDefineCount;
  DWORD dwData;
};

struct OwnshipData
{
  double latitudeDeg;
  double longitudeDeg;
  double indicatedAltitudeFt;
  double planeAltitudeFt;
  double groundVelocityKt;
  double trueHeadingDeg;
  double groundTrackDeg;
  double verticalSpeedFpm;
  int32_t simOnGround;
  char title[256];
  char atcModel[64];
  char atcId[64];
  char atcAirline[64];
  char atcFlightNumber[64];
  int32_t transponderCode;
};
#pragma pack(pop)

using SimConnectOpenFn = HRESULT(WINAPI*)(HANDLE*, LPCSTR, HWND, DWORD, HANDLE, DWORD);
using SimConnectCloseFn = HRESULT(WINAPI*)(HANDLE);
using SimConnectAddToDataDefinitionFn = HRESULT(WINAPI*)(HANDLE, DWORD, const char*, const char*, DWORD, float, DWORD);
using SimConnectSubscribeToSystemEventFn = HRESULT(WINAPI*)(HANDLE, DWORD, const char*);
using SimConnectRequestDataOnSimObjectFn = HRESULT(WINAPI*)(HANDLE, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
using SimConnectGetNextDispatchFn = HRESULT(WINAPI*)(HANDLE, SimConnectRecv**, DWORD*);

struct SimConnectApi
{
  HMODULE module = nullptr;
  SimConnectOpenFn open = nullptr;
  SimConnectCloseFn close = nullptr;
  SimConnectAddToDataDefinitionFn addToDataDefinition = nullptr;
  SimConnectSubscribeToSystemEventFn subscribeToSystemEvent = nullptr;
  SimConnectRequestDataOnSimObjectFn requestDataOnSimObject = nullptr;
  SimConnectGetNextDispatchFn getNextDispatch = nullptr;
};

struct TelemetrySnapshot
{
  std::string id;
  std::optional<std::string> callsign;
  std::optional<std::string> tailNumber;
  std::optional<std::string> aircraftTitle;
  std::optional<std::string> typeCode;
  std::optional<std::string> squawk;
  std::string simVersionLabel;
  double lat = 0;
  double lon = 0;
  double altBaroFt = 0;
  double altGeomFt = 0;
  double gsKt = 0;
  double headingDegTrue = 0;
  double trackDegTrue = 0;
  double vsFpm = 0;
  bool onGround = false;
  std::int64_t timestampMs = 0;
};

struct WorkerState
{
  std::optional<SimConnectApi> api;
  HANDLE handle = nullptr;
  bool requestedOwnshipStream = false;
  bool simFlightActive = false;
  std::string simApplicationName;
  std::chrono::steady_clock::time_point lastWaitingLogAt = std::chrono::steady_clock::time_point::min();
  std::chrono::steady_clock::time_point lastConnectionAttemptAt = std::chrono::steady_clock::time_point::min();
};

void OnSignal(int)
{
  g_running = 0;
}

std::string EscapeJson(const std::string& value)
{
  std::string escaped;
  escaped.reserve(value.size() + 8);

  for (const unsigned char ch : value)
  {
    switch (ch)
    {
      case '\\': escaped += "\\\\"; break;
      case '"': escaped += "\\\""; break;
      case '\n': escaped += "\\n"; break;
      case '\r': escaped += "\\r"; break;
      case '\t': escaped += "\\t"; break;
      default:
        if (ch < 0x20)
        {
          std::ostringstream hex;
          hex << "\\u" << std::uppercase << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(ch);
          escaped += hex.str();
        }
        else
        {
          escaped += static_cast<char>(ch);
        }
        break;
    }
  }

  return escaped;
}

void EmitStatus(const std::string& state, const std::string& message)
{
  std::cout
    << "{\"type\":\"status\",\"state\":\""
    << EscapeJson(state)
    << "\",\"message\":\""
    << EscapeJson(message)
    << "\"}"
    << std::endl;
}

void EmitWarning(const std::string& message)
{
  std::cout
    << "{\"type\":\"warning\",\"message\":\""
    << EscapeJson(message)
    << "\"}"
    << std::endl;
}

void EmitError(const std::string& message)
{
  std::cout
    << "{\"type\":\"error\",\"message\":\""
    << EscapeJson(message)
    << "\"}"
    << std::endl;
}

void EmitTelemetry(const TelemetrySnapshot& snapshot)
{
  std::ostringstream json;
  json << std::fixed << std::setprecision(8);
  json << "{\"type\":\"telemetry\",\"snapshot\":{";
  json << "\"id\":\"" << EscapeJson(snapshot.id) << "\"";

  auto appendOptionalString = [&](const char* name, const std::optional<std::string>& value)
  {
    json << ",\"" << name << "\":";
    if (value.has_value())
    {
      json << "\"" << EscapeJson(*value) << "\"";
    }
    else
    {
      json << "null";
    }
  };

  appendOptionalString("callsign", snapshot.callsign);
  appendOptionalString("tailNumber", snapshot.tailNumber);
  appendOptionalString("aircraftTitle", snapshot.aircraftTitle);
  appendOptionalString("typeCode", snapshot.typeCode);
  appendOptionalString("squawk", snapshot.squawk);
  json << ",\"simVersionLabel\":\"" << EscapeJson(snapshot.simVersionLabel) << "\"";
  json << ",\"lat\":" << snapshot.lat;
  json << ",\"lon\":" << snapshot.lon;
  json << ",\"altBaroFt\":" << snapshot.altBaroFt;
  json << ",\"altGeomFt\":" << snapshot.altGeomFt;
  json << ",\"gsKt\":" << snapshot.gsKt;
  json << ",\"headingDegTrue\":" << snapshot.headingDegTrue;
  json << ",\"trackDegTrue\":" << snapshot.trackDegTrue;
  json << ",\"vsFpm\":" << snapshot.vsFpm;
  json << ",\"onGround\":" << (snapshot.onGround ? "true" : "false");
  json << ",\"timestampMs\":" << snapshot.timestampMs;
  json << "}}";
  std::cout << json.str() << std::endl;
}

std::string Trim(const std::string& value)
{
  const auto first = value.find_first_not_of(" \t\r\n");
  if (first == std::string::npos)
  {
    return std::string();
  }

  const auto last = value.find_last_not_of(" \t\r\n");
  return value.substr(first, last - first + 1);
}

std::optional<std::string> NormalizeText(const std::string& value)
{
  const auto trimmed = Trim(value);
  if (trimmed.empty())
  {
    return std::nullopt;
  }

  return trimmed;
}

std::optional<std::string> NormalizeTypeCode(const std::string& value)
{
  const auto trimmed = NormalizeText(value);
  if (!trimmed.has_value())
  {
    return std::nullopt;
  }

  std::string compact;
  compact.reserve(trimmed->size());
  for (const unsigned char ch : *trimmed)
  {
    if (std::isalnum(ch))
    {
      compact.push_back(static_cast<char>(std::toupper(ch)));
    }
  }

  if (compact.size() < 3 || compact.size() > 5 || compact == "MSFS")
  {
    return std::nullopt;
  }

  return compact;
}

std::string ReadFixedString(const char* value, std::size_t length)
{
  std::size_t actual = 0;
  while (actual < length && value[actual] != '\0')
  {
    ++actual;
  }

  return std::string(value, actual);
}

bool IsValidCoordinates(double lat, double lon)
{
  return std::isfinite(lat) && std::isfinite(lon) && lat >= -90.0 && lat <= 90.0 && lon >= -180.0 && lon <= 180.0;
}

bool IsLikelyMenuPlaceholder(const OwnshipData& ownship)
{
  const bool nearNullIsland = std::abs(ownship.latitudeDeg) <= 0.05 && std::abs(ownship.longitudeDeg) <= 0.05;
  if (!nearNullIsland)
  {
    return false;
  }

  const bool lowAltitude = std::abs(ownship.indicatedAltitudeFt) <= 1000.0 && std::abs(ownship.planeAltitudeFt) <= 1000.0;
  const bool lowGroundSpeed = std::abs(ownship.groundVelocityKt) <= 30.0;
  return lowAltitude && lowGroundSpeed;
}

bool IsOctalDigits(const std::string& value)
{
  return std::all_of(value.begin(), value.end(), [](char ch) { return ch >= '0' && ch <= '7'; });
}

std::optional<std::string> FormatSquawk(int value)
{
  if (value < 0)
  {
    return std::nullopt;
  }

  if (value <= 7777)
  {
    std::ostringstream decimalCode;
    decimalCode << std::setfill('0') << std::setw(4) << value;
    if (IsOctalDigits(decimalCode.str()))
    {
      return decimalCode.str();
    }
  }

  if (value > 0x7777)
  {
    return std::nullopt;
  }

  const int d0 = value & 0xF;
  const int d1 = (value >> 4) & 0xF;
  const int d2 = (value >> 8) & 0xF;
  const int d3 = (value >> 12) & 0xF;
  if (d0 > 7 || d1 > 7 || d2 > 7 || d3 > 7)
  {
    return std::nullopt;
  }

  std::string code(4, '0');
  code[0] = static_cast<char>('0' + d3);
  code[1] = static_cast<char>('0' + d2);
  code[2] = static_cast<char>('0' + d1);
  code[3] = static_cast<char>('0' + d0);
  return code;
}

double NormalizeHeading(double heading)
{
  if (!std::isfinite(heading))
  {
    return 0.0;
  }

  double normalized = std::fmod(heading, 360.0);
  if (normalized < 0.0)
  {
    normalized += 360.0;
  }

  return normalized;
}

std::string ResolveSimVersionLabel(const std::string& applicationName, const std::string& fallback)
{
  const auto normalized = NormalizeText(applicationName);
  return normalized.has_value() ? *normalized : fallback;
}

std::optional<std::string> ResolveCallsign(const OwnshipData& ownship)
{
  const auto airline = NormalizeText(ReadFixedString(ownship.atcAirline, sizeof(ownship.atcAirline)));
  const auto flightNumber = NormalizeText(ReadFixedString(ownship.atcFlightNumber, sizeof(ownship.atcFlightNumber)));
  const auto atcId = NormalizeText(ReadFixedString(ownship.atcId, sizeof(ownship.atcId)));

  if (airline.has_value() && flightNumber.has_value())
  {
    return *airline + *flightNumber;
  }

  if (airline.has_value())
  {
    return airline;
  }

  if (atcId.has_value())
  {
    return atcId;
  }

  return flightNumber;
}

std::int64_t NowUnixMs()
{
  return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

int ReadEnvInt(const char* name, int fallback)
{
  const char* raw = std::getenv(name);
  if (raw == nullptr)
  {
    return fallback;
  }

  try
  {
    return std::stoi(raw);
  }
  catch (...)
  {
    return fallback;
  }
}

std::string ReadEnvString(const char* name, const std::string& fallback)
{
  const char* raw = std::getenv(name);
  return raw != nullptr ? std::string(raw) : fallback;
}

fs::path GetExecutableDirectory()
{
  std::wstring buffer(MAX_PATH, L'\0');
  const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
  buffer.resize(length);
  return fs::path(buffer).parent_path();
}

std::vector<fs::path> GetSimConnectCandidates()
{
  const fs::path exeDir = GetExecutableDirectory();
  return {
    exeDir / L"SimConnect.dll",
    exeDir / L"lib" / L"SimConnect.dll",
    exeDir / L".." / L".." / L".." / L"SimConnect.dll",
    exeDir / L".." / L".." / L".." / L"lib" / L"SimConnect.dll"
  };
}

template <typename T>
T LoadFunction(HMODULE module, const char* name)
{
  auto* procedure = reinterpret_cast<T>(GetProcAddress(module, name));
  if (procedure == nullptr)
  {
    throw std::runtime_error(std::string("Missing SimConnect export: ") + name);
  }

  return procedure;
}

SimConnectApi LoadSimConnectApi()
{
  for (const auto& candidate : GetSimConnectCandidates())
  {
    std::error_code ec;
    if (!fs::exists(candidate, ec))
    {
      continue;
    }

    HMODULE module = LoadLibraryW(candidate.c_str());
    if (module == nullptr)
    {
      continue;
    }

    SimConnectApi api;
    api.module = module;
    api.open = LoadFunction<SimConnectOpenFn>(module, "SimConnect_Open");
    api.close = LoadFunction<SimConnectCloseFn>(module, "SimConnect_Close");
    api.addToDataDefinition = LoadFunction<SimConnectAddToDataDefinitionFn>(module, "SimConnect_AddToDataDefinition");
    api.subscribeToSystemEvent = LoadFunction<SimConnectSubscribeToSystemEventFn>(module, "SimConnect_SubscribeToSystemEvent");
    api.requestDataOnSimObject = LoadFunction<SimConnectRequestDataOnSimObjectFn>(module, "SimConnect_RequestDataOnSimObject");
    api.getNextDispatch = LoadFunction<SimConnectGetNextDispatchFn>(module, "SimConnect_GetNextDispatch");
    return api;
  }

  throw std::runtime_error("SimConnect.dll not found near worker executable or bridge root.");
}

void FreeSimConnectApi(std::optional<SimConnectApi>& api)
{
  if (api.has_value() && api->module != nullptr)
  {
    FreeLibrary(api->module);
  }

  api.reset();
}

bool Succeeded(HRESULT hr)
{
  return SUCCEEDED(hr);
}

void CloseConnection(WorkerState& state)
{
  if (state.handle != nullptr && state.api.has_value())
  {
    state.api->close(state.handle);
  }

  state.handle = nullptr;
  state.requestedOwnshipStream = false;
  state.simFlightActive = false;
  state.simApplicationName.clear();
}

bool EnsureOwnshipDefinition(WorkerState& state)
{
  auto& api = *state.api;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "GPS GROUND TRUE TRACK", "degrees", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE_FLOAT64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE_INT32, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "TITLE", nullptr, SIMCONNECT_DATATYPE_STRING256, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "ATC MODEL", nullptr, SIMCONNECT_DATATYPE_STRING64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "ATC ID", nullptr, SIMCONNECT_DATATYPE_STRING64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "ATC AIRLINE", nullptr, SIMCONNECT_DATATYPE_STRING64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "ATC FLIGHT NUMBER", nullptr, SIMCONNECT_DATATYPE_STRING64, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  if (!Succeeded(api.addToDataDefinition(state.handle, kDefinitionOwnship, "TRANSPONDER CODE:1", "number", SIMCONNECT_DATATYPE_INT32, 0.0f, static_cast<DWORD>(kSimConnectUnused)))) return false;
  return true;
}

bool RegisterSystemEvents(WorkerState& state)
{
  auto& api = *state.api;
  return Succeeded(api.subscribeToSystemEvent(state.handle, kEventSimStart, "SimStart"))
    && Succeeded(api.subscribeToSystemEvent(state.handle, kEventSimStop, "SimStop"));
}

bool RequestOwnshipStream(WorkerState& state)
{
  if (state.requestedOwnshipStream)
  {
    return true;
  }

  auto& api = *state.api;
  const HRESULT hr = api.requestDataOnSimObject(
    state.handle,
    kRequestOwnship,
    kDefinitionOwnship,
    kObjectIdUser,
    SIMCONNECT_PERIOD_SIM_FRAME,
    kDataRequestChanged,
    0,
    0,
    0
  );

  if (!Succeeded(hr))
  {
    return false;
  }

  state.requestedOwnshipStream = true;
  return true;
}

void StopOwnshipStream(WorkerState& state)
{
  if (!state.requestedOwnshipStream || !state.api.has_value() || state.handle == nullptr)
  {
    return;
  }

  state.api->requestDataOnSimObject(
    state.handle,
    kRequestOwnship,
    kDefinitionOwnship,
    kObjectIdUser,
    SIMCONNECT_PERIOD_NEVER,
    0,
    0,
    0,
    0
  );
  state.requestedOwnshipStream = false;
}

bool TryConnect(WorkerState& state)
{
  const auto now = std::chrono::steady_clock::now();
  if (state.lastConnectionAttemptAt != std::chrono::steady_clock::time_point::min() && now - state.lastConnectionAttemptAt < std::chrono::seconds(2))
  {
    return false;
  }
  state.lastConnectionAttemptAt = now;

  if (!state.api.has_value())
  {
    try
    {
      state.api = LoadSimConnectApi();
    }
    catch (const std::exception& ex)
    {
      if (state.lastWaitingLogAt == std::chrono::steady_clock::time_point::min() || now - state.lastWaitingLogAt >= std::chrono::seconds(10))
      {
        EmitWarning(ex.what());
        state.lastWaitingLogAt = now;
      }
      return false;
    }
  }

  HRESULT hr = state.api->open(&state.handle, "AO MSFS Native Worker", nullptr, 0, nullptr, 0);
  if (!Succeeded(hr))
  {
    state.handle = nullptr;
    if (state.lastWaitingLogAt == std::chrono::steady_clock::time_point::min() || now - state.lastWaitingLogAt >= std::chrono::seconds(10))
    {
      EmitStatus("waiting_for_sim", "Waiting for MSFS + SimConnect...");
      state.lastWaitingLogAt = now;
    }
    return false;
  }

  if (!EnsureOwnshipDefinition(state) || !RegisterSystemEvents(state))
  {
    EmitError("Connected to SimConnect but failed to register data definition or events.");
    CloseConnection(state);
    return false;
  }

  state.lastWaitingLogAt = std::chrono::steady_clock::time_point::min();
  EmitStatus("ready", "Connected to SimConnect.");
  return true;
}

void HandleSimobjectData(WorkerState& state, const SimConnectRecvSimobjectData* message, const std::string& simVersionFallback)
{
  if (message->dwRequestID != kRequestOwnship)
  {
    return;
  }

  const auto* ownship = reinterpret_cast<const OwnshipData*>(&message->dwData);
  if (!IsValidCoordinates(ownship->latitudeDeg, ownship->longitudeDeg) || IsLikelyMenuPlaceholder(*ownship))
  {
    return;
  }

  state.simFlightActive = true;
  state.requestedOwnshipStream = true;

  TelemetrySnapshot snapshot;
  snapshot.id = "msfs_ownship";
  snapshot.callsign = ResolveCallsign(*ownship);
  snapshot.tailNumber = NormalizeText(ReadFixedString(ownship->atcId, sizeof(ownship->atcId)));
  snapshot.aircraftTitle = NormalizeText(ReadFixedString(ownship->title, sizeof(ownship->title)));
  snapshot.typeCode = NormalizeTypeCode(ReadFixedString(ownship->atcModel, sizeof(ownship->atcModel)));
  snapshot.squawk = FormatSquawk(ownship->transponderCode);
  snapshot.simVersionLabel = ResolveSimVersionLabel(state.simApplicationName, simVersionFallback);
  snapshot.lat = ownship->latitudeDeg;
  snapshot.lon = ownship->longitudeDeg;
  snapshot.altBaroFt = ownship->indicatedAltitudeFt;
  snapshot.altGeomFt = ownship->planeAltitudeFt;
  snapshot.gsKt = std::max(0.0, ownship->groundVelocityKt);
  snapshot.headingDegTrue = NormalizeHeading(ownship->trueHeadingDeg);
  snapshot.trackDegTrue = NormalizeHeading(ownship->groundTrackDeg);
  snapshot.vsFpm = ownship->verticalSpeedFpm;
  snapshot.onGround = ownship->simOnGround != 0;
  snapshot.timestampMs = NowUnixMs();
  EmitTelemetry(snapshot);
}

void PollDispatch(WorkerState& state, const std::string& simVersionFallback)
{
  if (state.handle == nullptr || !state.api.has_value())
  {
    return;
  }

  while (g_running == 1)
  {
    SimConnectRecv* dispatch = nullptr;
    DWORD dispatchSize = 0;
    const HRESULT hr = state.api->getNextDispatch(state.handle, &dispatch, &dispatchSize);
    if (!Succeeded(hr) || dispatch == nullptr)
    {
      break;
    }

    switch (dispatch->dwId)
    {
      case SIMCONNECT_RECV_ID_OPEN:
      {
        const auto* open = reinterpret_cast<const SimConnectRecvOpen*>(dispatch);
        state.simApplicationName = ReadFixedString(open->szApplicationName, sizeof(open->szApplicationName));
        EmitStatus("ready", std::string("SimConnect session opened: ") + state.simApplicationName);
        RequestOwnshipStream(state);
        break;
      }
      case SIMCONNECT_RECV_ID_EVENT:
      {
        const auto* eventMessage = reinterpret_cast<const SimConnectRecvEvent*>(dispatch);
        if (eventMessage->uEventID == kEventSimStart)
        {
          state.simFlightActive = true;
          RequestOwnshipStream(state);
        }
        else if (eventMessage->uEventID == kEventSimStop)
        {
          state.simFlightActive = false;
          StopOwnshipStream(state);
          EmitStatus("waiting_for_sim", "SimStop received. Waiting for next flight.");
        }
        break;
      }
      case SIMCONNECT_RECV_ID_SIMOBJECT_DATA:
      {
        const auto* simObjectData = reinterpret_cast<const SimConnectRecvSimobjectData*>(dispatch);
        HandleSimobjectData(state, simObjectData, simVersionFallback);
        break;
      }
      case SIMCONNECT_RECV_ID_EXCEPTION:
      {
        const auto* exceptionMessage = reinterpret_cast<const SimConnectRecvException*>(dispatch);
        EmitWarning(std::string("SimConnect exception code: ") + std::to_string(exceptionMessage->dwException));
        break;
      }
      case SIMCONNECT_RECV_ID_QUIT:
        EmitStatus("waiting_for_sim", "MSFS session closed. Waiting for simulator restart...");
        CloseConnection(state);
        return;
      default:
        break;
    }
  }
}
} // namespace

int main(int argc, char** argv)
{
  std::signal(SIGINT, OnSignal);
  std::signal(SIGTERM, OnSignal);

  for (int index = 1; index < argc; ++index)
  {
    const std::string argument = argv[index];
    if (argument == "--version")
    {
      std::cout << "msfs-simconnect-worker 0.2.0" << std::endl;
      return 0;
    }
  }

  const int pollIntervalMs = std::max(5, ReadEnvInt("MSFS_BRIDGE_POLL_MS", 25));
  const std::string simVersionFallback = ReadEnvString("MSFS_BRIDGE_SIM_VERSION_FALLBACK", "Local Bridge");

  EmitStatus("starting", "Native SimConnect worker booted.");

  WorkerState state;

  while (g_running == 1)
  {
    if (state.handle == nullptr)
    {
      TryConnect(state);
    }
    else
    {
      PollDispatch(state, simVersionFallback);
      if (state.handle != nullptr)
      {
        std::this_thread::sleep_for(std::chrono::milliseconds(pollIntervalMs));
      }
    }

    if (state.handle == nullptr)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(250));
    }
  }

  CloseConnection(state);
  FreeSimConnectApi(state.api);
  EmitStatus("stopping", "Worker shutdown requested.");
  return 0;
}


