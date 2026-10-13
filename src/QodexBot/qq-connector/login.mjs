import { startQrConnect } from '@tencent-connect/qqbot-connector';
import QRCode from 'qrcode';

const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`);

let stop = () => {};
stop = startQrConnect({
  async onQrDisplayed(url) {
    try {
      const svg = await QRCode.toString(url, {
        type: 'svg',
        margin: 2,
        width: 320,
        color: { dark: '#14213d', light: '#ffffff' },
      });
      send({ type: 'qr', svg });
    } catch (error) {
      send({ type: 'failure', message: error instanceof Error ? error.message : String(error) });
      setTimeout(() => process.exit(1), 50);
    }
  },
  onSuccess(credentials) {
    send({ type: 'success', credentials });
    stop();
    setTimeout(() => process.exit(0), 50);
  },
  onFailure(error) {
    send({ type: 'failure', message: error instanceof Error ? error.message : String(error) });
    setTimeout(() => process.exit(1), 50);
  },
}, { displayQrCodeToConsole: false });

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    stop();
    process.exit(0);
  });
}
