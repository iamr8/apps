namespace apps.Models;

[Flags]
public enum OS : byte
{
    None = 0,
    MacOS = 1,
    Windows = 2,
}