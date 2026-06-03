namespace apps;

[Flags]
public enum AppAttribute
{
    None = 0,

    App = 131072,
    ElectronApp = 1,
    MacCatalystApp = 4,
    HomebrewCask = 256,
    IosOrIpadApp = 8,

    PwaApp = 16,

    SparkleFeed = 2,
    AppStoreApp = 32,
    MacApp = 64,

    SafariExtension = 128,
    ChromeExtension = 4096,
    VsCodeExtension = 2048,
    JetBrainsPlugin = 8192,

    Sdk = 16384,
    DevTool = 32768,
    Image = 65536,
    Library = 1024,
    HomebrewFormula = 512,
}