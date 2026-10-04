namespace Gideon.PowerOverhaul.Util
{
    public static class MathUtil
    {
        public static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
