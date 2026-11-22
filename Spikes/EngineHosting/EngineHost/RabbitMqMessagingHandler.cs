using System.Text;
using Newtonsoft.Json;
using QuantConnect.Packets;

namespace QuantConnect.Spikes.EngineHosting
{
    /// <summary>
    /// Keeps the stock console messaging (STATISTICS:: lines, OrderListHash) and also publishes
    /// the final backtest result packet and runtime errors to the results queue.
    /// </summary>
    public class RabbitMqMessagingHandler : Messaging.Messaging
    {
        public override void Send(Packet packet)
        {
            base.Send(packet);

            if (packet is BacktestResultPacket { Progress: 1 } result)
            {
                SpikeBus.Publish("backtest-result", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result)));
            }
            else if (packet.Type == PacketType.RuntimeError)
            {
                SpikeBus.Publish("runtime-error", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packet)));
            }
        }
    }
}
