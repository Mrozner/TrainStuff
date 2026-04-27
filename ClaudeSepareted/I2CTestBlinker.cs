// DEPRECATED: This class is NO LONGER USED and should NOT be called.
// It was sending rogue light/signal commands that interfered with the proper LightController.
// The I2C Test Blinker has been removed from MqttInfrastructureService initialization.
// Use LightsFromDatabase + LightController for proper signal control instead.

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;

public static class I2CTestBlinker
{
    private static bool isRed = false; // Megakadályozza, hogy feleslegesen spammeljük a Pirost, ha a vonat hosszú

    public static async Task StartBlinkingInBackground(IMqttClient mqttClient)
    {
        Console.WriteLine("[TESZT] Automatikus Szenzorvezérelt Hálózat Indítása...");

        // 1. ALAPÁLLAPOT BEÁLLÍTÁSA: Minden lámpa ZÖLD / FEHÉR
        await SetAllSignals(mqttClient, isGreen: true);
        Console.WriteLine("🟢 ALAPÁLLAPOT: Minden jelző SZABADRA állítva.");

        // 2. ESEMÉNYKEZELŐ: Figyeljük a Hall szenzorok MQTT üzeneteit a Pythontól
        mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            string topic = e.ApplicationMessage.Topic;
            if (topic == "track/sensor/hall")
            {
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                // Ha a payload-ban ott a "trainDetected": true, ÉS még nem vagyunk piroson
                if (payload.Contains("\"trainDetected\": true") && !isRed)
                {
                    isRed = true;
                    Console.WriteLine("\n🚂 [RIASZTÁS] Vonat érzékelve a Hall szenzoron! Minden jelző TILOSRA vált!");

                    // Azonnal pirosra (megállj) vágjuk az összes lámpát
                    await SetAllSignals(mqttClient, isGreen: false);

                    // Háttérszálon elindítunk egy időzítőt: 10 másodperc múlva magától visszavált Zöldre
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10000); // 10 másodperc (10000 ms) piros fázis
                        Console.WriteLine("\n🟢 [TISZTA] Szakasz felszabadult (10 mp letelt). Visszatérés SZABAD állásba.");
                        await SetAllSignals(mqttClient, isGreen: true);
                        isRed = false;
                    });
                }
            }
        };

        // Feliratkozunk a szenzor csatornára, hogy meghalljuk a Python hidat
        await mqttClient.SubscribeAsync("track/sensor/hall");
        Console.WriteLine("[RENDSZER] Feliratkozva a 'track/sensor/hall' csatornára. Várakozás a vonatokra...");

        // Egy végtelen ciklus, hogy a Task életben maradjon, miközben az eseménykezelő teszi a dolgát
        while (true)
        {
            await Task.Delay(1000);
        }
    }

    // --- KÖZÖS METÓDUS A LÁMPÁK VÁLTÁSÁHOZ (ZÖLD VAGY PIROS) ---
    private static async Task SetAllSignals(IMqttClient client, bool isGreen)
    {
        string topic = "track/command/signal";
        int mega1_Id = 8;
        int mega2_Id = 9;

        // Változók beállítása aszerint, hogy Zöldet vagy Pirost kérünk
        // 52 = Max sebesség (Zöld / Fehér), 48 = Megállj (Piros / Kék)
        int speedCmd = isGreen ? 52 : 48;

        // --- MEGA 1 CSOMAGOK ---
        int[] cmd_m1_lampa1 = { 83, 49, 32, speedCmd, 32, speedCmd };
        int[] cmd_m1_lampa2 = { 83, 50, 32, speedCmd, 32, speedCmd };
        int[] cmd_m1_lampa3 = { 83, 51, 32, speedCmd, 32, speedCmd };
        int[] cmd_m1_lampa4 = { 83, 52, 32, speedCmd, 32, speedCmd }; // 7-ledes!
        int[] cmd_m1_vaganyzaro = { 83, 53, 32, speedCmd, 32, speedCmd }; // Fehér vagy Piros

        // Fénysorompók (F parancs: 'F' = 70, ID, szóköz, állapot) -> 49 = Szabad, 48 = Tilos
        int fsCmd = isGreen ? 49 : 48;
        int[] cmd_m1_fs1 = { 70, 49, 32, fsCmd };
        int[] cmd_m1_fs2 = { 70, 50, 32, fsCmd };

        // --- MEGA 2 CSOMAGOK ---
        int[] cmd_m2_lampa1 = { 83, 49, 32, speedCmd, 32, speedCmd }; // 7-ledes!
        int[] cmd_m2_lampa2 = { 83, 50, 32, speedCmd, 32, speedCmd }; // 7-ledes!
        int[] cmd_m2_tolato = { 83, 51, 32, speedCmd, 32, speedCmd }; // Fehér vagy Kék

        // --- KÜLDÉS MEGA 1-NEK ---
        await SendMessage(client, topic, mega1_Id, cmd_m1_lampa1);
        await SendMessage(client, topic, mega1_Id, cmd_m1_lampa2);
        await SendMessage(client, topic, mega1_Id, cmd_m1_lampa3);
        await SendMessage(client, topic, mega1_Id, cmd_m1_lampa4);
        await SendMessage(client, topic, mega1_Id, cmd_m1_vaganyzaro);
        await SendMessage(client, topic, mega1_Id, cmd_m1_fs1);
        await SendMessage(client, topic, mega1_Id, cmd_m1_fs2);

        // --- KÜLDÉS MEGA 2-NEK ---
        await SendMessage(client, topic, mega2_Id, cmd_m2_lampa1);
        await SendMessage(client, topic, mega2_Id, cmd_m2_lampa2);
        await SendMessage(client, topic, mega2_Id, cmd_m2_tolato);
    }

    // --- SEGÉDMETÓDUS AZ MQTT KÜLDÉSHEZ ---
    private static async Task SendMessage(IMqttClient client, string topic, int boardId, int[] bytes)
    {
        var cmd = new { boardId = boardId, bytes = bytes };
        string json = JsonSerializer.Serialize(cmd);
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(json).Build();

        if (client != null && client.IsConnected)
        {
            await client.PublishAsync(message, CancellationToken.None);
            await Task.Delay(150); // 150ms szünet a stabil átvitelhez
        }
    }
}