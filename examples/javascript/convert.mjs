import { createWriteStream } from 'node:fs';

const baseUrl = process.env.INACTIVEPDF_URL ?? 'http://127.0.0.1:5080';
const token = process.env.INACTIVEPDF_TOKEN;
if (!token) throw new Error('Set INACTIVEPDF_TOKEN before running this example.');

const form = new FormData();
form.set('mode', 'async');
form.set('profile', 'archive');
form.set('file', new Blob([await (await import('node:fs/promises')).readFile(process.argv[2] ?? 'document.docx')]), process.argv[2] ?? 'document.docx');
const accepted = await fetch(`${baseUrl}/v1/conversions`, {
  method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': `example-${Date.now()}` }, body: form
});
if (!accepted.ok) throw new Error(`${accepted.status}: ${await accepted.text()}`);
const job = await accepted.json();
let status;
do {
  await new Promise(resolve => setTimeout(resolve, 500));
  const response = await fetch(`${baseUrl}/v1/jobs/${job.jobId}`, { headers: { Authorization: `Bearer ${token}` } });
  status = await response.json();
} while (!['Succeeded', 'Failed', 'Cancelled', 'DeadLettered', 'Interrupted'].includes(status.state));
if (status.state !== 'Succeeded') throw new Error(`Conversion ended in ${status.state}: ${status.errorCode ?? 'unknown error'}`);
const output = await fetch(`${baseUrl}/v1/jobs/${job.jobId}/output`, { headers: { Authorization: `Bearer ${token}` } });
if (!output.ok) throw new Error(`${output.status}: ${await output.text()}`);
const file = createWriteStream('converted.pdf');
for await (const chunk of output.body) file.write(chunk);
file.end();
console.log(`Converted ${status.jobId} to converted.pdf`);
