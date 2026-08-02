import com.microsoft.signalr.HubConnection;
import com.microsoft.signalr.HubConnectionBuilder;
import com.microsoft.signalr.TransportEnum;
import io.reactivex.rxjava3.core.Single;
import io.reactivex.rxjava3.subjects.SingleSubject;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

// Phase 5 D30/D34 client probe (Java, com.microsoft.signalr 9.0.6). Implements exactly the
// scenario in tests/Keryhe.Switchboard.CompatibilityTests/ClientProbeContract.md, restricted to
// the cells the Java client SDK actually supports: TransportEnum has no
// SERVER_SENT_EVENTS member at all (only WEBSOCKETS/LONG_POLLING/ALL), and the "signalr" jar ships
// only GsonHubProtocol — no MessagePack hub protocol class exists in it. Both are real SDK
// limitations, not probe bugs: SSE and MessagePack cells are "not applicable" for this SDK, the
// same way SSE+MessagePack is "not applicable" for the service itself (03-protocol.md §1.5).
//
// Usage: java -cp ... Probe <apiBaseUrl> <transport> <protocol>
public class Probe {
    private static final List<String> completedSteps = new ArrayList<>();

    public static void main(String[] args) {
        try {
            run(args);
        } catch (Exception ex) {
            String step = completedSteps.isEmpty() ? "connect" : nextStep(completedSteps.get(completedSteps.size() - 1));
            ex.printStackTrace();
            System.out.println("RESULT FAIL step=" + step + " reason=" + ex.getClass().getSimpleName() + ":" + ex.getMessage());
            System.exit(1);
        }
    }

    private static void run(String[] args) throws Exception {
        if (args.length != 3) {
            System.out.println("RESULT FAIL step=args reason=expected-3-arguments");
            System.exit(1);
            return;
        }

        String apiBaseUrl = args[0];
        String transportArg = args[1].toLowerCase(Locale.ROOT);
        if ("sse".equals(transportArg)) {
            // No SERVER_SENT_EVENTS member exists on this SDK's TransportEnum at all — see the
            // class doc comment.
            System.out.println("RESULT FAIL step=connect reason=NotApplicable:java-sdk-has-no-sse-transport");
            System.exit(1);
            return;
        }

        TransportEnum transport = parseTransport(transportArg);
        boolean useMessagePack = parseProtocol(args[2]);
        if (useMessagePack) {
            // No MessagePack hub protocol ships in this SDK version — see the class doc comment.
            System.out.println("RESULT FAIL step=connect reason=NotApplicable:java-sdk-has-no-messagepack-hub-protocol");
            System.exit(1);
            return;
        }

        String suffix = Long.toHexString(System.nanoTime());
        String roomId = "probe-room-" + suffix;

        String accessToken = login(apiBaseUrl, "probe-" + suffix);

        HubConnection connection = HubConnectionBuilder.create(apiBaseUrl + "/chatHub")
                .withTransport(transport)
                .withAccessTokenProvider(Single.just(accessToken))
                .build();

        SingleSubject<String> connected = SingleSubject.create();
        connection.on("Connected", id -> connected.onSuccess(id), String.class);

        SingleSubject<ChatMessage> groupMessage = SingleSubject.create();
        connection.on("ReceiveMessage", msg -> groupMessage.onSuccess(msg), ChatMessage.class);

        if (!connection.start().blockingAwait(15, TimeUnit.SECONDS)) {
            throw new RuntimeException("timeout:connect");
        }

        completedSteps.add("connect");

        connected.timeout(10, TimeUnit.SECONDS).blockingGet();
        completedSteps.add("receive_push");

        if (!connection.invoke("JoinRoom", roomId).blockingAwait(10, TimeUnit.SECONDS)) {
            throw new RuntimeException("timeout:join_group");
        }

        completedSteps.add("join_group");

        if (!connection.invoke("SendMessage", roomId, "probe-payload").blockingAwait(10, TimeUnit.SECONDS)) {
            throw new RuntimeException("timeout:invoke");
        }

        completedSteps.add("invoke");

        ChatMessage message = groupMessage.timeout(10, TimeUnit.SECONDS).blockingGet();
        if (!"probe-payload".equals(message.text)) {
            System.out.println("RESULT FAIL step=receive_group reason=unexpected-payload:" + message.text);
            System.exit(1);
            return;
        }

        completedSteps.add("receive_group");

        connection.stop().blockingAwait(10, TimeUnit.SECONDS);
        completedSteps.add("disconnect");

        System.out.println("RESULT OK steps=" + String.join(",", completedSteps));
    }

    private static String login(String apiBaseUrl, String username) throws Exception {
        HttpClient http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build();
        String body = "{\"username\":\"" + username + "\"}";
        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(apiBaseUrl + "/api/auth/login"))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .timeout(Duration.ofSeconds(10))
                .build();

        HttpResponse<String> response = http.send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2) {
            throw new RuntimeException("login-failed:" + response.statusCode());
        }

        Matcher matcher = Pattern.compile("\"accessToken\"\\s*:\\s*\"([^\"]+)\"").matcher(response.body());
        if (!matcher.find()) {
            throw new RuntimeException("login-response-missing-accessToken");
        }

        return matcher.group(1);
    }

    private static TransportEnum parseTransport(String value) {
        switch (value) {
            case "websockets":
                return TransportEnum.WEBSOCKETS;
            case "longpolling":
                return TransportEnum.LONG_POLLING;
            default:
                throw new IllegalArgumentException("Unknown transport '" + value + "'.");
        }
    }

    private static boolean parseProtocol(String value) {
        switch (value.toLowerCase(Locale.ROOT)) {
            case "json":
                return false;
            case "messagepack":
                return true;
            default:
                throw new IllegalArgumentException("Unknown protocol '" + value + "'.");
        }
    }

    private static String nextStep(String lastCompleted) {
        List<String> order = List.of("connect", "receive_push", "join_group", "invoke", "receive_group", "disconnect");
        int index = order.indexOf(lastCompleted);
        return (index < 0 || index == order.size() - 1) ? "unknown" : order.get(index + 1);
    }

    // Mirrors ChatHub.SendMessage's anonymous push shape (From, Text, SentAt) — only the field the
    // scenario asserts on is required; Gson ignores unknown/extra JSON fields by default.
    public static class ChatMessage {
        public String from;
        public String text;
    }
}
