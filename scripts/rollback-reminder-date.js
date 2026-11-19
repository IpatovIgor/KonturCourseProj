// Run with mongosh --nodb --file scripts/rollback-reminder-date.js after stopping writers.
const uri = process.env.HABIT_TRACKER_ROLLBACK_MONGO_URI;
const databaseName = process.env.HABIT_TRACKER_ROLLBACK_DATABASE;
const role = process.env.HABIT_TRACKER_ROLLBACK_ROLE;
const collectionName = role === 'webapi' ? 'habits' : role === 'worker' ? 'habitSchedules' : null;

if (!uri || !databaseName || !collectionName || ['admin', 'config', 'local'].includes(databaseName)) {
    throw new Error('Set the rollback MongoDB URI, application database and role (webapi or worker).');
}

const database = connect(uri).getSiblingDB(databaseName);
if (!database.getCollectionNames().includes(collectionName)) {
    throw new Error(`Collection ${databaseName}.${collectionName} does not exist.`);
}

const collection = database.getCollection(collectionName);
const filter = { reminderFirstEligibleDate: { $exists: true } };
const result = collection.updateMany(filter, { $unset: { reminderFirstEligibleDate: '' } });
print(`${databaseName}.${collectionName}: ${result.modifiedCount} document(s) updated`);
