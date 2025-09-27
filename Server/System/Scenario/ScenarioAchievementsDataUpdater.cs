using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        /// <summary>
        /// We received an achievement message so update the scenario file accordingly
        /// </summary>
        public static void WriteAchievementDataToFile(ShareProgressAchievementsMsgData achievementMsg)
        {
            Task.Run(() =>
            {
                lock (Semaphore.GetOrAdd("ProgressTracking", new object()))
                {
                    if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ProgressTracking", out var scenario)) return;

                    var progressNodeHeader = scenario.GetNode("Progress").Value;
                    if (progressNodeHeader != null)
                    {
                        var specificNode = progressNodeHeader.GetNode(achievementMsg.Id);
                        var receivedNode = new ConfigNode(Encoding.UTF8.GetString(achievementMsg.Data, 0, achievementMsg.NumBytes)) { Name = achievementMsg.Id };
                        DeduplicateCrewInConfigNode(receivedNode);
                        if (specificNode != null)
                        {
                            progressNodeHeader.ReplaceNode(specificNode.Value, receivedNode);
                        }
                        else
                        {
                            progressNodeHeader.AddNode(receivedNode);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Deduplicates crew members within a ConfigNode.
        /// </summary>
        private static void DeduplicateCrewInConfigNode(ConfigNode node)
        {
            foreach (var subNodeValue in node.GetNodes("crew"))
            {
                var subNode = subNodeValue.Value; // subNode is a ConfigNode

                var crewsCfgNodeValue = subNode.GetValue("crews"); // This returns CfgNodeValue<string, string>

                if (crewsCfgNodeValue != null && !string.IsNullOrWhiteSpace(crewsCfgNodeValue.Value))
                {
                    var crewsString = crewsCfgNodeValue.Value;
                    var distinctCrews = crewsString.Split(',').Select(c => c.Trim()).Distinct().Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                    crewsCfgNodeValue.Value = string.Join(", ", distinctCrews); // Attempt to set the Value property of CfgNodeValue
                }
            }
        }
    }
}
