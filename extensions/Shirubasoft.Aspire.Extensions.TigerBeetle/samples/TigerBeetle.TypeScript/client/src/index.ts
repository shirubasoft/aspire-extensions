import { createServer } from 'node:http';
import { lookup } from 'node:dns/promises';
import { isIP } from 'node:net';
import { createClient } from 'tigerbeetle-node';

const clusterId = process.env.TIGERBEETLE_CLUSTERID;
const addresses = process.env.TIGERBEETLE_ADDRESSES;

if (!clusterId) {
  throw new Error('TIGERBEETLE_CLUSTERID is required.');
}

if (!addresses) {
  throw new Error('TIGERBEETLE_ADDRESSES is required.');
}

const client = createClient({
  cluster_id: BigInt(clusterId),
  replica_addresses: await Promise.all(addresses.split(',').map(value => resolveAddress(value.trim())))
});

const port = Number(process.env.PORT ?? '3001');

createServer(async (request, response) => {
  response.setHeader('content-type', 'application/json');

  if (request.url?.startsWith('/accounts/')) {
    const id = BigInt(request.url.slice('/accounts/'.length));
    const accounts = await client.lookupAccounts([id]);
    response.end(JSON.stringify(accounts, (_, value) => typeof value === 'bigint' ? value.toString() : value));
    return;
  }

  response.end(JSON.stringify({
    clusterId,
    addresses,
    message: 'TigerBeetle connection properties were injected by Aspire.'
  }));
}).listen(port, '0.0.0.0');

async function resolveAddress(value: string): Promise<string> {
  if (/^\d+$/.test(value)) {
    return value;
  }

  const endpoint = new URL(`tcp://${value}`);
  const hostname = endpoint.hostname.startsWith('[') && endpoint.hostname.endsWith(']')
    ? endpoint.hostname.slice(1, -1)
    : endpoint.hostname;

  if (isIP(hostname)) {
    return value;
  }

  const resolved = await lookup(hostname);
  return resolved.family === 6
    ? `[${resolved.address}]:${endpoint.port}`
    : `${resolved.address}:${endpoint.port}`;
}
