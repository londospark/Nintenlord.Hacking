namespace Nintenlord.Hacking.Core
{
    public static class BitUtility
    {
        public static bool IsValidIntOffset(int value) => (value & 3) == 0;

        public static bool IsInvalidIntOffset(int value) => !IsValidIntOffset(value);

        public static bool IsValidShortOffset(int value) => (value & 1) == 0;

        public static bool IsInvalidShortOffset(int value) => !IsValidShortOffset(value);
    }
}
