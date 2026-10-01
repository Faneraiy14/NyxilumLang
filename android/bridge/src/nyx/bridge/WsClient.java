package nyx.bridge;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Base64;

import javax.net.ssl.SSLSocketFactory;

// Мінімальний WebSocket-клієнт (RFC 6455) на java.net - без бібліотек:
// ws:// і wss://, handshake, текстові кадри, ping/pong, close. Те, що
// потрібно для "⚡ миттєвої доставки": сервер шле короткі текстові
// сигнали ("sync", "ping"), клієнт лише слухає.
// Чиста Java без android.* - перевіряється на звичайній JVM.
public final class WsClient {
    public interface Listener {
        void onText(String text);
    }

    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;
    private final SecureRandom rnd = new SecureRandom();

    // readTimeoutMs - скільки чекати на будь-який кадр (сервер пінгує кожні 30 с)
    public WsClient(String url, int readTimeoutMs) throws Exception {
        URI u = new URI(url);
        boolean tls = "wss".equalsIgnoreCase(u.getScheme());
        int port = u.getPort() > 0 ? u.getPort() : (tls ? 443 : 80);
        String host = u.getHost();
        Socket s;
        if (tls) {
            s = SSLSocketFactory.getDefault().createSocket();
            s.connect(new InetSocketAddress(host, port), 15000);
            ((javax.net.ssl.SSLSocket) s).startHandshake();
        } else {
            s = new Socket();
            s.connect(new InetSocketAddress(host, port), 15000);
        }
        s.setSoTimeout(readTimeoutMs);
        socket = s;
        in = new BufferedInputStream(s.getInputStream());
        out = s.getOutputStream();

        byte[] keyBytes = new byte[16];
        rnd.nextBytes(keyBytes);
        String key = Base64.getEncoder().encodeToString(keyBytes);
        String path = (u.getRawPath() == null || u.getRawPath().isEmpty() ? "/" : u.getRawPath())
                + (u.getRawQuery() != null ? "?" + u.getRawQuery() : "");
        String hostHeader = host + ((tls && port == 443) || (!tls && port == 80) ? "" : ":" + port);
        String req = "GET " + path + " HTTP/1.1\r\n"
                + "Host: " + hostHeader + "\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Key: " + key + "\r\n"
                + "Sec-WebSocket-Version: 13\r\n\r\n";
        out.write(req.getBytes(StandardCharsets.US_ASCII));
        out.flush();

        String status = readLine();
        if (status == null || !status.contains(" 101")) {
            close();
            throw new Exception("WebSocket: сервер відповів " + status);
        }
        // заголовки відповіді - до порожнього рядка
        String line;
        while ((line = readLine()) != null && !line.isEmpty()) { }
    }

    private String readLine() throws Exception {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        int c;
        while ((c = in.read()) != -1) {
            if (c == '\n') break;
            if (c != '\r') b.write(c);
        }
        if (c == -1 && b.size() == 0) return null;
        return b.toString("UTF-8");
    }

    private int readByte() throws Exception {
        int c = in.read();
        if (c == -1) throw new EOFException("з'єднання закрито");
        return c;
    }

    // Слухати до закриття/помилки (кидає виняток - тоді перепідключатися)
    public void listen(Listener listener) throws Exception {
        ByteArrayOutputStream msg = new ByteArrayOutputStream();
        while (true) {
            int b0 = readByte();
            int b1 = readByte();
            boolean fin = (b0 & 0x80) != 0;
            int opcode = b0 & 0x0F;
            long len = b1 & 0x7F;
            if (len == 126) {
                len = (readByte() << 8) | readByte();
            } else if (len == 127) {
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | readByte();
            }
            if (len > 1_000_000) throw new Exception("WebSocket: завеликий кадр");
            byte[] mask = null;
            if ((b1 & 0x80) != 0) {
                mask = new byte[4];
                for (int i = 0; i < 4; i++) mask[i] = (byte) readByte();
            }
            byte[] data = new byte[(int) len];
            for (int i = 0; i < len; i++) {
                int v = readByte();
                data[i] = (byte) (mask == null ? v : v ^ mask[i % 4]);
            }
            if (opcode == 8) {           // close
                sendFrame(8, new byte[0]);
                throw new EOFException("сервер закрив з'єднання");
            } else if (opcode == 9) {    // ping -> pong
                sendFrame(10, data);
            } else if (opcode == 10) {   // pong
                // нічого
            } else if (opcode == 1 || opcode == 0) {
                msg.write(data);
                if (fin) {
                    listener.onText(msg.toString("UTF-8"));
                    msg.reset();
                }
            }
        }
    }

    // Клієнт зобов'язаний маскувати кадри
    private synchronized void sendFrame(int opcode, byte[] payload) throws Exception {
        ByteArrayOutputStream f = new ByteArrayOutputStream();
        f.write(0x80 | opcode);
        int len = payload.length;
        if (len < 126) {
            f.write(0x80 | len);
        } else {
            f.write(0x80 | 126);
            f.write((len >> 8) & 0xFF);
            f.write(len & 0xFF);
        }
        byte[] mask = new byte[4];
        rnd.nextBytes(mask);
        f.write(mask);
        for (int i = 0; i < len; i++) f.write(payload[i] ^ mask[i % 4]);
        out.write(f.toByteArray());
        out.flush();
    }

    public void sendText(String text) throws Exception {
        sendFrame(1, text.getBytes(StandardCharsets.UTF_8));
    }

    public void close() {
        try { socket.close(); } catch (Exception ignored) { }
    }
}
