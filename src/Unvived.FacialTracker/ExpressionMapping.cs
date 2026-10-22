using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace Unvived.FacialTracker;

/// <summary>SRanipal lip shapes to VRCFaceTracking's Unified Expressions.</summary>
internal static class ExpressionMapping
{
    public static void Apply(float[] w, UnifiedExpressionShape[] shapes)
    {
        float Get(LipShape s) => w[(int)s];
        void Set(UnifiedExpressions e, float v) => shapes[(int)e].Weight = Math.Clamp(v, 0f, 1f);

        // Jaw. "Ape shape" is the jaw dropped with the lips kept together.
        Set(UnifiedExpressions.JawOpen, Get(LipShape.JawOpen) + Get(LipShape.MouthApeShape));
        Set(UnifiedExpressions.MouthClosed, Get(LipShape.MouthApeShape));
        Set(UnifiedExpressions.JawRight, Get(LipShape.JawRight));
        Set(UnifiedExpressions.JawLeft, Get(LipShape.JawLeft));
        Set(UnifiedExpressions.JawForward, Get(LipShape.JawForward));

        // Lips shifting sideways.
        Set(UnifiedExpressions.MouthUpperRight, Get(LipShape.MouthUpperRight));
        Set(UnifiedExpressions.MouthUpperLeft, Get(LipShape.MouthUpperLeft));
        Set(UnifiedExpressions.MouthLowerRight, Get(LipShape.MouthLowerRight));
        Set(UnifiedExpressions.MouthLowerLeft, Get(LipShape.MouthLowerLeft));

        // Upper lip raised / lower lip lowered; an overturned lip is not a raised one.
        float upperOverturn = Get(LipShape.MouthUpperOverturn);
        float lowerOverturn = Get(LipShape.MouthLowerOverturn);
        float upperUpRight = Get(LipShape.MouthUpperUpRight) - upperOverturn;
        float upperUpLeft = Get(LipShape.MouthUpperUpLeft) - upperOverturn;
        Set(UnifiedExpressions.MouthUpperUpRight, upperUpRight);
        Set(UnifiedExpressions.MouthUpperUpLeft, upperUpLeft);
        Set(UnifiedExpressions.MouthUpperDeepenRight, upperUpRight);
        Set(UnifiedExpressions.MouthUpperDeepenLeft, upperUpLeft);
        Set(UnifiedExpressions.MouthLowerDownRight, Get(LipShape.MouthLowerDownRight) - lowerOverturn);
        Set(UnifiedExpressions.MouthLowerDownLeft, Get(LipShape.MouthLowerDownLeft) - lowerOverturn);

        // Overturned lips are funnelled; pout is a pucker.
        Set(UnifiedExpressions.LipFunnelUpperRight, upperOverturn);
        Set(UnifiedExpressions.LipFunnelUpperLeft, upperOverturn);
        Set(UnifiedExpressions.LipFunnelLowerRight, lowerOverturn);
        Set(UnifiedExpressions.LipFunnelLowerLeft, lowerOverturn);
        float pout = Get(LipShape.MouthPout);
        Set(UnifiedExpressions.LipPuckerUpperRight, pout);
        Set(UnifiedExpressions.LipPuckerUpperLeft, pout);
        Set(UnifiedExpressions.LipPuckerLowerRight, pout);
        Set(UnifiedExpressions.LipPuckerLowerLeft, pout);

        // Lips rolled in, and the lower lip pushed over the upper one.
        float upperInside = Get(LipShape.MouthUpperInside);
        float lowerInside = Get(LipShape.MouthLowerInside);
        Set(UnifiedExpressions.LipSuckUpperRight, upperInside);
        Set(UnifiedExpressions.LipSuckUpperLeft, upperInside);
        Set(UnifiedExpressions.LipSuckLowerRight, lowerInside);
        Set(UnifiedExpressions.LipSuckLowerLeft, lowerInside);
        float overlay = Get(LipShape.MouthLowerOverlay);
        Set(UnifiedExpressions.MouthRaiserLower, overlay);
        Set(UnifiedExpressions.MouthRaiserUpper, overlay - upperInside);

        // Smile and frown.
        float smileRight = Get(LipShape.MouthSmileRight);
        float smileLeft = Get(LipShape.MouthSmileLeft);
        Set(UnifiedExpressions.MouthCornerPullRight, smileRight);
        Set(UnifiedExpressions.MouthCornerPullLeft, smileLeft);
        Set(UnifiedExpressions.MouthCornerSlantRight, smileRight);
        Set(UnifiedExpressions.MouthCornerSlantLeft, smileLeft);
        Set(UnifiedExpressions.CheekSquintRight, smileRight);
        Set(UnifiedExpressions.CheekSquintLeft, smileLeft);
        Set(UnifiedExpressions.MouthDimpleRight, smileRight);
        Set(UnifiedExpressions.MouthDimpleLeft, smileLeft);
        float sadRight = Get(LipShape.MouthSadRight);
        float sadLeft = Get(LipShape.MouthSadLeft);
        Set(UnifiedExpressions.MouthFrownRight, sadRight);
        Set(UnifiedExpressions.MouthFrownLeft, sadLeft);
        Set(UnifiedExpressions.MouthStretchRight, sadRight);
        Set(UnifiedExpressions.MouthStretchLeft, sadLeft);

        // Cheeks.
        Set(UnifiedExpressions.CheekPuffRight, Get(LipShape.CheekPuffRight));
        Set(UnifiedExpressions.CheekPuffLeft, Get(LipShape.CheekPuffLeft));
        float cheekSuck = Get(LipShape.CheekSuck);
        Set(UnifiedExpressions.CheekSuckRight, cheekSuck);
        Set(UnifiedExpressions.CheekSuckLeft, cheekSuck);

        // Tongue. The two "long" steps are partly and fully out.
        Set(UnifiedExpressions.TongueOut, (Get(LipShape.TongueLongStep1) + Get(LipShape.TongueLongStep2)) / 2f);
        Set(UnifiedExpressions.TongueUp, Get(LipShape.TongueUp));
        Set(UnifiedExpressions.TongueDown, Get(LipShape.TongueDown));
        Set(UnifiedExpressions.TongueRight, Get(LipShape.TongueRight));
        Set(UnifiedExpressions.TongueLeft, Get(LipShape.TongueLeft));
        Set(UnifiedExpressions.TongueRoll, Get(LipShape.TongueRoll));
    }
}
