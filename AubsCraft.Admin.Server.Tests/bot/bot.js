// A real Minecraft Java Edition client (mineflayer) for the live tests.
// Usage: node bot.js <host> <port> <username> [version]
// Prints one JSON object per line on stdout: {"event":"spawn"}, {"event":"message","text":...},
// {"event":"kicked","reason":...}, {"event":"end","reason":...}, {"event":"error","message":...}.
// Reads commands from stdin, one per line: "chat <text>" (a "/..." text runs a command), "walk <ms>" (walks
// forward the way it faces for that long, like a player stepping away) or "quit".
const mineflayer = require('mineflayer');
const readline = require('readline');

const [host, port, username, version] = process.argv.slice(2);
const out = o => process.stdout.write(JSON.stringify(o) + '\n');

const bot = mineflayer.createBot({ host, port: Number(port), username, version: version || '1.21.5', auth: 'offline' });
bot.on('spawn', () => out({ event: 'spawn' }));
bot.on('messagestr', text => out({ event: 'message', text }));
bot.on('kicked', reason => out({ event: 'kicked', reason: typeof reason === 'string' ? reason : JSON.stringify(reason) }));
bot.on('end', reason => { out({ event: 'end', reason: String(reason) }); process.exit(0); });
bot.on('error', err => out({ event: 'error', message: String(err && err.message || err) }));

readline.createInterface({ input: process.stdin }).on('line', line => {
  if (line === 'quit') { bot.quit(); return; }
  if (line.startsWith('chat ')) bot.chat(line.slice(5));
  if (line.startsWith('walk ')) {
    bot.setControlState('forward', true);
    setTimeout(() => bot.setControlState('forward', false), Number(line.slice(5)));
  }
});
