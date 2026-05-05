using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSepareted.Services;

namespace ClaudeSepareted.Lights
{
    /// <summary>
    /// MQTT alapú lámpavezérlő.
    /// Csak továbbítja a kapott sebességadatokat az Arduinónak, a színlogikát a hardver végzi.
    /// Uses 150ms delay between MQTT publishes to allow hardware bus processing.
    /// </summary>
    public class LightController
    {
        private readonly MqttInfrastructureService _mqttService;
        private readonly string _topic = "track/command/signal";

        public LightController(MqttInfrastructureService mqttService)
        {
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
        }

        /// <summary>
        /// Megkeresi a lámpá(ka)t és továbbítja nekik a két sebességkódot.
        /// </summary>
        public async Task SetLightSpeedAsync(
            string Section,
            string beforeSection,
            bool dir,
            int currentSpeedAscii,
            int nextSpeedAscii,
            int? mega = null,
            int? lightId = null,
            CancellationToken cancellationToken = default)
        {
            // 1. Lámpák azonosítása
            List<(int mega, int id)> targetLights = new List<(int mega, int id)>();

            if (mega.HasValue && lightId.HasValue)
            {
                targetLights.Add((mega.Value, lightId.Value));
            }
            else
            {
                // Lekérjük az összes lámpát ami ehhez a szekcióhoz és irányhoz tartozik
                targetLights = ObjectsLibrary.GetLightInfo(Section, dir);
            }

            // 2. Parancs küldése a főjelző(k)nek
            if (targetLights.Count > 0)
            {
                foreach (var light in targetLights)
                {
                    Console.WriteLine($"[LÁMPA] {beforeSection} -> {Section} | Mega: {light.mega}, ID: {light.id} | Jelenlegi seb: {currentSpeedAscii}, Következő seb: {nextSpeedAscii}");
                    await SendCommandAsync(light.mega, light.id, currentSpeedAscii, nextSpeedAscii, cancellationToken);
                }
            }
            else
            {
                // Ha direktben egy szekciót kérdezünk le (pl. a bootolásnál), ez a log normális lehet, ha nincs oda fizikai lámpa bekötve
                Console.WriteLine($"[HIBA/INFO] Nem találtam lámpát ehhez: {Section} (Irány: {dir})");
            }

            // 3. Fénysorompók szinkronizálása a szakaszon
            // Ha a jelenlegi szakaszra a sebesség 48 (Megállj), a fénysorompó is tilosra (48) vált.
            // Minden más sebességnél a fénysorompó szabad/fehér (49) marad.
            if ((Section == "P26.2") ||
                (Section == "P26.3"))
            {
                int fsState = currentSpeedAscii == 48 ? 48 : 49;
                Console.WriteLine($"[FÉNYSOROMPÓ] {beforeSection} -> {Section} | 1-es és 2-es fénysorompó -> {(fsState == 48 ? "PIROS" : "FEHÉR")}");
                await SendFenysorompoCommandAsync(8, 1, fsState, cancellationToken);
                await SendFenysorompoCommandAsync(8, 2, fsState, cancellationToken);
            }
        }

        private async Task SendCommandAsync(
            int megaId,
            int lampaId,
            int speedCmdAscii1,
            int speedCmdAscii2,
            CancellationToken cancellationToken = default)
        {
            int idAscii = lampaId + 48;
            int[] bytes = { 83, idAscii, 32, speedCmdAscii1, 32, speedCmdAscii2 };

            var cmd = new { boardId = megaId, bytes = bytes };
            string jsonString = JsonSerializer.Serialize(cmd);

            // A MqttInfrastructureService meglévő metódusát használjuk, mint a RocrailCommandService
            bool success = await _mqttService.PublishAsync(_topic, jsonString);

            if (success)
            {
                await Task.Delay(150, cancellationToken); // CRITICAL: Allow hardware bus to process
                Console.WriteLine($"   -> [MQTT KÜLDVE] {jsonString}");
            }
            else
            {
                Console.WriteLine($"   -> [HIBA] MQTT küldés sikertelen (SendCommandAsync)!");
            }
        }

        private async Task SendFenysorompoCommandAsync(
            int megaId,
            int fsId,
            int stateCmdAscii,
            CancellationToken cancellationToken = default)
        {
            int idAscii = fsId + 48;
            int[] bytes = { 70, idAscii, 32, stateCmdAscii };

            var cmd = new { boardId = megaId, bytes = bytes };
            string jsonString = JsonSerializer.Serialize(cmd);

            // A MqttInfrastructureService meglévő metódusát használjuk
            bool success = await _mqttService.PublishAsync(_topic, jsonString);

            if (success)
            {
                await Task.Delay(150, cancellationToken); // CRITICAL: Allow hardware bus to process
                Console.WriteLine($"   -> [MQTT FS KÜLDVE] {jsonString}");
            }
            else
            {
                Console.WriteLine($"   -> [HIBA] MQTT küldés sikertelen (SendFenysorompoCommandAsync)!");
            }
        }
    }
}