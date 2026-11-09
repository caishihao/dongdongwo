using BongoCat.Core.Util;

namespace BongoCat.Core.Util;

/// <summary>
/// 洗牌袋（Shuffle Bag）：从池中不重复随机取元素，取空后重新洗牌。
/// 对应原作"音效随机池"机制（分析报告 §2 / ADR-006 规则 3），原创实现。
/// </summary>
/// <typeparam name="T">池元素类型。</typeparam>
public sealed class ShuffleBag<T>
{
    private readonly List<T> _items;
    private readonly List<T> _bag = [];
    private readonly Random _random;

    /// <param name="items">池元素（至少 1 个）。</param>
    /// <param name="random">可注入随机源（种子可复现，Core 编码规则）。</param>
    public ShuffleBag(IReadOnlyList<T> items, Random random)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("洗牌袋至少需要 1 个元素。", nameof(items));
        }

        _items = [.. items];
        _random = random;
    }

    /// <summary>取下一个元素：袋空时整袋重洗，保证池内元素轮换出现。</summary>
    public T Next()
    {
        if (_bag.Count == 0)
        {
            _bag.AddRange(_items);
            // Fisher-Yates 洗牌
            for (int i = _bag.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
        }

        int last = _bag.Count - 1;
        T item = _bag[last];
        _bag.RemoveAt(last);
        return item;
    }
}
