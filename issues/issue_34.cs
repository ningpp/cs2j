
using System.Collections.Generic;

namespace Demo {
public class Issue_34 {
    public IList<String> Inners { get; set; }
    IEnumerable<PlacementStrategy> placementStrategyPriority = new[] {
                                                                            PlacementStrategy.Horizontal,
                                                                            PlacementStrategy.AlongCurve
                                                                        };

    public IEnumerable<PlacementStrategy> PlacementStrategyPriority {
        get { return placementStrategyPriority; }
        set { placementStrategyPriority = value; }
    }
    public enum PlacementStrategy {
        AlongCurve,

        Horizontal
    }
}
}