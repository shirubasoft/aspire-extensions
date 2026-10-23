import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tigerBeetle = await builder
  .addTigerBeetle('tigerbeetle')
  .withDataVolume()
  .withCacheGrid('256MiB');

await builder
  .addNodeApp('client', './client', 'dist/index.js')
  .withReference(tigerBeetle)
  .waitFor(tigerBeetle)
  .withHttpEndpoint({ env: 'PORT' })
  .withExternalHttpEndpoints();

// This compile-only function keeps the generated CDC API covered without adding
// RabbitMQ to the running TypeScript sample.
async function verifyChangeDataCaptureExports() {
  const rabbitMq = builder.addRabbitMQ('rabbitmq-typecheck');
  const connectionString = builder.addConnectionString('rabbitmq-connection-typecheck');

  await tigerBeetle
    .addChangeDataCapture('tigerbeetle-cdc-typecheck', rabbitMq, 'tigerbeetle')
    .withVirtualHost('/')
    .withPublishRoutingKey('transfers')
    .withTimestampLast('0')
    .withCdcArgs(['--event-count-max=100', '--idle-interval-ms=250']);

  await tigerBeetle.addChangeDataCapture(
    'tigerbeetle-cdc-connection-typecheck',
    connectionString,
    'tigerbeetle');
}

void verifyChangeDataCaptureExports;

await builder.build().run();
