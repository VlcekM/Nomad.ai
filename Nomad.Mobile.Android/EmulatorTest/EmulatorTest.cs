#if NOMAD_EMULATOR_TEST
using Android.App;

// Emulator test builds only: the app may use plain HTTP to the PC running the emulator (see network_security_config.xml).
[assembly: Application(NetworkSecurityConfig = "@xml/network_security_config")]
#endif
