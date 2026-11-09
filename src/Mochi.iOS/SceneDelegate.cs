using Foundation;
using UIKit;

namespace Mochi.iOS;

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    PetViewController? Pet => Window?.RootViewController as PetViewController;

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window ??= new UIWindow(windowScene);
        Window.RootViewController = new PetViewController();
        Window.MakeKeyAndVisible();
    }

    [Export("sceneDidBecomeActive:")]
    public void DidBecomeActive(UIScene scene) => Pet?.Resume();

    // Switching apps, locking the phone or pulling down Control Center.
    [Export("sceneWillResignActive:")]
    public void WillResignActive(UIScene scene) => Pet?.Pause();
}
