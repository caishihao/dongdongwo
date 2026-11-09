namespace BongoCat.App.Styles;

/// <summary>
/// 首启风格选择器触发策略（GDD-08 §3.1，纯逻辑可单测）：
/// 触发条件 = TutorialCompleted==false 且 StyleChosen==false（两个独立标记，
/// 选过风格但没做完教程的玩家重进不重复弹选择器；教程完成的老玩家永不打扰）。
/// </summary>
public static class StylePickerPolicy
{
    public static bool ShouldShow(bool tutorialCompleted, bool styleChosen)
        => !tutorialCompleted && !styleChosen;
}
