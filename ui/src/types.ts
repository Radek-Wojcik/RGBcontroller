// Mirrors of the IPC records (src/RGBcontroller.Ipc, src/RGBcontroller.Core/Config) in their JSON form:
// camelCase properties, enums as strings.

export type ComponentState = "Unknown" | "Starting" | "Ok" | "Degraded" | "Offline";
export type AmbilightMode = "Screen" | "Radial" | "Solid" | "Off";
export type EffectKind = "Off" | "Static" | "Breathing" | "Wave" | "Radial" | "Mirror" | "ColorCycle" | "Aurora" | "Hardware";
export type RadialMode = "Rotate" | "Expand";
export type StripCorner = "TopLeft" | "TopRight" | "BottomRight" | "BottomLeft";
export type HotkeyAction =
  | "StripSolidToggle" | "StripBrightnessUp" | "StripBrightnessDown" | "CycleStripMode"
  | "NextProfile" | "PreviousProfile" | "Profile" | "AllOffToggle";

export interface ServiceStatus {
  openRgb: ComponentState;
  strip: ComponentState;
  detail: string | null;
  activeProfile: string | null;
}

export interface AgentState {
  sequence: number;
  status: ServiceStatus;
  profiles: string[];
  activeProfile: string;
  shownProfile: string;
  stripMode: AmbilightMode;
  stripBrightnessPercent: number;
  captureWanted: boolean;
  allOff: boolean;
  settingsRevision: number;
  configRevision: number;
}

export interface CommandResult { success: boolean; message: string | null }

export interface ZoneInfo { name: string; type: number; ledCount: number; resizable: boolean; matrixWidth: number; matrixHeight: number }
export interface DeviceInfo { index: number; name: string; type: number; zones: ZoneInfo[] }
export interface DeviceList { devices: DeviceInfo[]; stripLedCount: number }

export interface StripLayoutConfig {
  startCorner: StripCorner;
  clockwise: boolean;
  top: number;
  right: number;
  bottom: number;
  left: number;
  offset: number;
  gaps: number[];
}

export interface StripConfig {
  enabled: boolean;
  portName: string | null;
  usbVendorId: string;
  usbProductId: string;
  ledCount: number;
  baudRate: number;
  colorOrder: string;
  maxFps: number;
  openSettleMilliseconds: number;
  currentLimitMilliamps: number;
  keepAliveMilliseconds: number;
  layout: StripLayoutConfig;
}

export interface ScreenCaptureConfig {
  monitor: string | null;
  captureFps: number;
  outputFps: number;
  minimumSampleWidth: number;
  edgeDepthCm: number;
  screenWidthCm: number;
  segmentOverlapPercent: number;
  blackThreshold: number;
  blackKnee: number;
  gamma: number;
  saturation: number;
  attackMilliseconds: number;
  releaseMilliseconds: number;
  blackReleaseMilliseconds: number;
  fadeInMilliseconds: number;
  letterboxDetection: boolean;
  letterboxStableMilliseconds: number;
  hdrSdrWhiteNits: number;
  fallbackGraceMilliseconds: number;
}

export interface HotkeyBinding { keys: string; action: HotkeyAction; profile: string | null }
export interface HotkeysConfig { bindings: HotkeyBinding[]; brightnessStepPercent: number }

export interface TurnOffSet { strip: boolean; devices: string[] }
export interface DisplayOffConfig { unlocked: TurnOffSet; locked: TurnOffSet }

export interface ZoneRef { device: string; zone: string | null }
export interface GroupConfig { name: string; members: ZoneRef[]; maxFps: number }
export interface PaletteStop { position: number; color: string }
export interface PaletteConfig { name: string; stops: PaletteStop[] }

export interface GroupEffectConfig {
  group: string;
  effect: EffectKind;
  palette: string;
  speed: number;
  angle: number;
  reverse: boolean;
  scale: number;
  radialMode: RadialMode;
  brightnessPercent: number;
  hardwareMode: string | null;
}

export interface AmbilightConfig {
  mode: AmbilightMode;
  fallback: AmbilightMode;
  brightnessPercent: number;
  solidColor: string;
  solidBrightnessPercent: number;
  palette: string;
  speed: number;
}

export interface ProfileConfig {
  name: string;
  masterBrightnessPercent: number;
  groups: GroupEffectConfig[];
  ambilight: AmbilightConfig;
}

export interface LightingConfig {
  activeProfile: string;
  logonProfile: string;
  lockProfile: string | null;
  displayOff: DisplayOffConfig;
  defaultGroup: string | null;
  layouts: unknown[];
  groups: GroupConfig[];
  palettes: PaletteConfig[];
  profiles: ProfileConfig[];
}

export interface AppConfig {
  schemaVersion: number;
  strip: StripConfig;
  openRgb: Record<string, unknown>;
  lighting: LightingConfig;
  capture: ScreenCaptureConfig;
  hotkeys: HotkeysConfig;
  logging: Record<string, unknown>;
}

export interface ConfigSnapshot { revision: number; config: AppConfig }
export interface ConfigUpdateResult { success: boolean; message: string | null; revision: number; errors: string[]; restartRequired: boolean }

export interface StripChangeRequest {
  mode?: AmbilightMode;
  brightnessPercent?: number;
  brightnessDelta?: number;
  toggleSolid?: boolean;
  cycleMode?: boolean;
}

export interface GroupTuningRequest {
  group: string;
  profile?: string;
  maxFps?: number;
  speed?: number;
  scale?: number;
  brightnessPercent?: number;
}

export interface Connection { connected: boolean; version: string | null; error: string | null }

/** Every method of the UI bridge (src/RGBcontroller.UI/Bridge.cs) with its parameters and result. */
export interface BridgeMethods {
  getStatus: [undefined, ServiceStatus];
  getState: [undefined, AgentState];
  getProfiles: [undefined, { profiles: string[]; activeProfile: string }];
  setActiveProfile: [{ name: string }, CommandResult];
  stepProfile: [{ delta: number }, CommandResult];
  tuneGroup: [GroupTuningRequest, CommandResult];
  changeStrip: [StripChangeRequest, CommandResult];
  setAllOff: [{ off: boolean }, CommandResult];
  showStripTest: [{ seconds: number }, CommandResult];
  getAgentSettings: [undefined, unknown];
  getConfig: [undefined, ConfigSnapshot];
  updateConfig: [{ expectedRevision: number; config: AppConfig }, ConfigUpdateResult];
  getDevices: [undefined, DeviceList];
}
