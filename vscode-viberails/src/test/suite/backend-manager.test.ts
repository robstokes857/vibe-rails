import * as assert from 'assert/strict';
import { BackendManager } from '../../backend-manager';

suite('BackendManager', () => {
    test('root launch args stay detached from the extension host PID', async () => {
        const manager = new BackendManager('vb.exe');

        try {
            const args = (manager as any).buildLaunchArgs() as string[];

            assert.deepEqual(args, ['--vs-code-v1']);
            assert.ok(!args.includes('--parent-pid'));
        } finally {
            await manager.shutdown();
        }
    });
});
