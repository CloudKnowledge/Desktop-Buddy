namespace Mochi.Core.Rig;

/// <summary>
/// Every continuous parameter of Mochi's rig. Behaviours write a *target* pose each frame and the
/// <see cref="Animation.PoseAnimator"/> eases the *current* pose toward it, so any combination of
/// states blends smoothly without frame pops.
/// </summary>
public sealed class PetPose
{
    // Posture blends, 0..1
    public float Sit, Lie, Curl, Bow, Held;
    public float Crouch;         // hunting stalk: belly low, head forward
    public float Arch;           // startled "Halloween cat" arch
    public float Leap;           // stretched mid-air pounce
    public float Front;          // sitting square-on, facing the viewer
    public float PawsUp;         // front paws held up at the chest (with Front)

    // Whole body
    public float Hop;            // height above ground, model units
    public float Squash;         // >0 squash (landing), <0 stretch
    public float BodyTilt;       // extra pitch, degrees
    public float Breath;         // -1..1, driven by animator
    public float Wiggle;         // whole-body shake, degrees
    public float Flip;           // whole-body rotation about the belly (twist jumps), degrees
    public float Puff;           // 0..1 fur standing on end
    public float Purr;           // 0..1 purr vibration (driven by animator)

    // Gait
    public float GaitPhase;      // radians, advanced by distance travelled
    public float GaitAmount;     // 0 still, 1 walk, 2 run

    // Head
    public float HeadTurn;       // -1 look back .. 0 viewer .. 1 look forward
    public float HeadTilt;       // degrees
    public float HeadLift;       // -1 down .. 1 up
    public float HeadDx, HeadDy; // extra lean in model units

    // Eyes
    public float EyeOpen = 1;
    public float EyeOpenL = 1, EyeOpenR = 1;   // per-eye multipliers (wink / peek)
    public float HappyEyes;                    // 0..1 "^ ^"
    public float LookX, LookY;                 // -1..1 pupil direction
    public float EyeSize = 1;
    public float PupilSize = 0.72f;            // 0 slit .. 1 round "hunting" pupils
    public float Sparkle;                      // extra shine 0..1

    // Brow whiskers, mouth, whiskers
    public float BrowRaise;      // -1..1
    public float BrowSad;        // 0..1
    public float MouthOpen;      // 0..1
    public float Smile = 0.4f;   // -1..1
    public float Tongue;         // 0..1 (small values = blep)
    public float Blush = 0.4f;   // 0..1
    public float Whisker;        // -1 swept back .. 1 fanned forward

    // Ears
    public float EarLift;        // -1 flattened .. 1 perked forward
    public float EarAsym;        // -1..1 one ear swivels
    public float EarFlapL, EarFlapR; // degrees, spring-driven flicks

    // Tail
    public float TailRaise = 0.6f;   // -1 tucked .. 1 straight up
    public float TailSway;           // amplitude, degrees
    public float TailSwaySpeed = 0.9f;// Hz
    public float TailSwayPhase;      // radians, animator
    public float TailCurl = 0.4f;    // 0..1 hook at the tip ("question mark")
    public float TailWrap;           // 0..1 wrapped around the paws

    // Paws
    public float FrontPawRaise;      // near front paw lifted (wave / swat) 0..1
    public float PawToFace;          // near front paw up at the mouth (grooming) 0..1
    public float Knead;              // 0..1 alternating front-paw kneading
    public float PawPaddle;          // legs kicking while held / dreaming 0..1
    public float PaddlePhase;        // radians, animator
    public float Wave;               // 0..1 near paw up, waving hello
    public float Glasses;            // 0..1 little round work glasses
    public float LightsPhase;        // radians, animator (fairy-light twinkle)

    // Held pendulum (degrees) and grab point in model units
    public float SwingAngle;
    public float GrabX, GrabY = -58;

    public PetPose Clone() => (PetPose)MemberwiseClone();

    public void CopyFrom(PetPose o)
    {
        Sit = o.Sit; Lie = o.Lie; Curl = o.Curl; Bow = o.Bow; Held = o.Held; Crouch = o.Crouch; Arch = o.Arch; Leap = o.Leap;
        Front = o.Front; PawsUp = o.PawsUp;
        Hop = o.Hop; Squash = o.Squash; BodyTilt = o.BodyTilt; Breath = o.Breath; Wiggle = o.Wiggle; Flip = o.Flip; Puff = o.Puff; Purr = o.Purr;
        GaitPhase = o.GaitPhase; GaitAmount = o.GaitAmount;
        HeadTurn = o.HeadTurn; HeadTilt = o.HeadTilt; HeadLift = o.HeadLift; HeadDx = o.HeadDx; HeadDy = o.HeadDy;
        EyeOpen = o.EyeOpen; EyeOpenL = o.EyeOpenL; EyeOpenR = o.EyeOpenR; HappyEyes = o.HappyEyes;
        LookX = o.LookX; LookY = o.LookY; EyeSize = o.EyeSize; PupilSize = o.PupilSize; Sparkle = o.Sparkle;
        BrowRaise = o.BrowRaise; BrowSad = o.BrowSad; MouthOpen = o.MouthOpen; Smile = o.Smile; Tongue = o.Tongue; Blush = o.Blush; Whisker = o.Whisker;
        EarLift = o.EarLift; EarAsym = o.EarAsym; EarFlapL = o.EarFlapL; EarFlapR = o.EarFlapR;
        TailRaise = o.TailRaise; TailSway = o.TailSway; TailSwaySpeed = o.TailSwaySpeed; TailSwayPhase = o.TailSwayPhase; TailCurl = o.TailCurl; TailWrap = o.TailWrap;
        FrontPawRaise = o.FrontPawRaise; PawToFace = o.PawToFace; Knead = o.Knead; PawPaddle = o.PawPaddle; PaddlePhase = o.PaddlePhase;
        LightsPhase = o.LightsPhase; Wave = o.Wave; Glasses = o.Glasses;
        SwingAngle = o.SwingAngle; GrabX = o.GrabX; GrabY = o.GrabY;
    }

    /// <summary>Resets the target to Mochi's relaxed standing defaults.</summary>
    public void ResetToNeutral()
    {
        Sit = Lie = Curl = Bow = Held = Crouch = Arch = Leap = Front = PawsUp = 0;
        Hop = Squash = BodyTilt = Wiggle = Flip = Puff = 0;
        GaitAmount = 0;
        HeadTurn = 0.12f; HeadTilt = 0; HeadLift = 0; HeadDx = HeadDy = 0;
        EyeOpen = 1; EyeOpenL = EyeOpenR = 1; HappyEyes = 0; LookX = 0.1f; LookY = 0.1f; EyeSize = 1; PupilSize = 0.72f; Sparkle = 0;
        BrowRaise = 0; BrowSad = 0; MouthOpen = 0; Smile = 0.4f; Tongue = 0; Blush = 0.4f; Whisker = 0;
        EarLift = 0; EarAsym = 0;
        TailRaise = 0.6f; TailSway = 8; TailSwaySpeed = 0.9f; TailCurl = 0.4f; TailWrap = 0;
        FrontPawRaise = 0; PawToFace = 0; Knead = 0; PawPaddle = 0; Wave = 0; Glasses = 0;
        SwingAngle = 0;
    }

    /// <summary>Eases this pose toward <paramref name="t"/>. Fields driven procedurally are copied directly.</summary>
    public void Approach(PetPose t, float dt)
    {
        float posture = Rate(7f, dt), body = Rate(14f, dt), head = Rate(9f, dt), face = Rate(16f, dt), eye = Rate(22f, dt), slow = Rate(4f, dt);

        Sit = Lerp(Sit, t.Sit, posture); Lie = Lerp(Lie, t.Lie, posture); Curl = Lerp(Curl, t.Curl, Rate(4f, dt));
        Bow = Lerp(Bow, t.Bow, posture); Held = Lerp(Held, t.Held, Rate(10f, dt));
        Crouch = Lerp(Crouch, t.Crouch, posture); Arch = Lerp(Arch, t.Arch, Rate(12f, dt)); Leap = Lerp(Leap, t.Leap, Rate(14f, dt));
        Front = Lerp(Front, t.Front, Rate(9f, dt)); PawsUp = Lerp(PawsUp, t.PawsUp, Rate(8f, dt));

        Hop = t.Hop; Squash = Lerp(Squash, t.Squash, Rate(26f, dt)); BodyTilt = Lerp(BodyTilt, t.BodyTilt, body);
        Wiggle = t.Wiggle; Flip = t.Flip; Puff = Lerp(Puff, t.Puff, Rate(t.Puff > Puff ? 18f : 3f, dt));
        Purr = Lerp(Purr, t.Purr, Rate(4f, dt));
        GaitAmount = Lerp(GaitAmount, t.GaitAmount, Rate(6f, dt));

        HeadTurn = Lerp(HeadTurn, t.HeadTurn, head); HeadTilt = Lerp(HeadTilt, t.HeadTilt, head);
        HeadLift = Lerp(HeadLift, t.HeadLift, head); HeadDx = Lerp(HeadDx, t.HeadDx, head); HeadDy = Lerp(HeadDy, t.HeadDy, head);

        EyeOpen = Lerp(EyeOpen, t.EyeOpen, eye); EyeOpenL = Lerp(EyeOpenL, t.EyeOpenL, eye); EyeOpenR = Lerp(EyeOpenR, t.EyeOpenR, eye);
        HappyEyes = Lerp(HappyEyes, t.HappyEyes, face);
        LookX = Lerp(LookX, t.LookX, eye); LookY = Lerp(LookY, t.LookY, eye);
        EyeSize = Lerp(EyeSize, t.EyeSize, face); PupilSize = Lerp(PupilSize, t.PupilSize, Rate(5f, dt)); Sparkle = Lerp(Sparkle, t.Sparkle, face);

        BrowRaise = Lerp(BrowRaise, t.BrowRaise, face); BrowSad = Lerp(BrowSad, t.BrowSad, face);
        MouthOpen = Lerp(MouthOpen, t.MouthOpen, face); Smile = Lerp(Smile, t.Smile, face);
        Tongue = Lerp(Tongue, t.Tongue, face); Blush = Lerp(Blush, t.Blush, slow); Whisker = Lerp(Whisker, t.Whisker, face);

        EarLift = Lerp(EarLift, t.EarLift, face); EarAsym = Lerp(EarAsym, t.EarAsym, face);

        TailRaise = Lerp(TailRaise, t.TailRaise, Rate(6f, dt)); TailSway = Lerp(TailSway, t.TailSway, Rate(4f, dt));
        TailSwaySpeed = Lerp(TailSwaySpeed, t.TailSwaySpeed, Rate(4f, dt)); TailCurl = Lerp(TailCurl, t.TailCurl, Rate(6f, dt));
        TailWrap = Lerp(TailWrap, t.TailWrap, Rate(4f, dt));

        FrontPawRaise = Lerp(FrontPawRaise, t.FrontPawRaise, body); PawToFace = Lerp(PawToFace, t.PawToFace, Rate(9f, dt));
        Knead = Lerp(Knead, t.Knead, Rate(5f, dt)); PawPaddle = Lerp(PawPaddle, t.PawPaddle, Rate(6f, dt));
        Wave = Lerp(Wave, t.Wave, Rate(8f, dt));
        Glasses = Lerp(Glasses, t.Glasses, Rate(6f, dt));
        SwingAngle = t.SwingAngle; GrabX = t.GrabX; GrabY = t.GrabY;
    }

    static float Rate(float perSecond, float dt) => 1f - MathF.Exp(-perSecond * dt);
    static float Lerp(float a, float b, float k) => a + (b - a) * k;
}
