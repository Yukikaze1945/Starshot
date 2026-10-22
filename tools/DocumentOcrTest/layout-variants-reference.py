"""Isolated official Paddle vs ONNX/C# parity. Never shipped with the application."""
import sys,pathlib,json,time
import numpy as np
import cv2
import onnxruntime as ort
import paddle.inference as pi
root,models,report=map(pathlib.Path,sys.argv[1:])
native=json.loads((report/'inference.json').read_text())
results=[]
for v in ['m','l']:
    rgb=cv2.cvtColor(cv2.imread(str(root/'page.png')),cv2.COLOR_BGR2RGB)
    t=cv2.resize(rgb,(640,640),interpolation=cv2.INTER_CUBIC).astype(np.float32)/255
    if v=='m':t=(t-np.array([.485,.456,.406],np.float32))/np.array([.229,.224,.225],np.float32)
    t=t.transpose(2,0,1)[None]
    cs=np.fromfile(root/(v+'-input.bin'),dtype=np.float32).reshape(t.shape)
    diff=np.abs(t-cs)
    # Byte rounding may differ by one at cubic ties; require <=1 byte and tiny mean.
    max_error=float(diff.max());mean_error=float(diff.mean())
    assert max_error<=1/255/.224+1e-5 and mean_error<.0001,(v,max_error,mean_error)
    d=models/('official-'+v)
    config=pi.Config(str(d/'inference.json'),str(d/'inference.pdiparams'));config.disable_gpu();config.set_cpu_math_library_num_threads(2);config.disable_glog_info()
    predictor=pi.create_predictor(config)
    feed={'image':cs,'scale_factor':np.array([[640/1380,640/972]],np.float32),'im_shape':np.array([[640,640]],np.float32)}
    for name in predictor.get_input_names():h=predictor.get_input_handle(name);h.reshape(feed[name].shape);h.copy_from_cpu(feed[name])
    start=time.perf_counter();predictor.run();elapsed=(time.perf_counter()-start)*1000
    outputs=[predictor.get_output_handle(n).copy_to_cpu() for n in predictor.get_output_names()]
    det=next(x for x in outputs if x.ndim==2 and x.shape[-1]==6);det=det[np.isfinite(det).all(1)&(det[:,1]>=.3)]
    item=next(x for x in native if x.get('variant')==v and 'regions' in x and isinstance(x['regions'],list));matches=[]
    for region in item['regions']:
        box=region['Box'];a=np.array([box['X'],box['Y'],box['X']+box['Width'],box['Y']+box['Height']]);best=0;score_diff=None
        for b in det[det[:,0]==region['ClassId']]:
            bb=np.maximum(0,np.minimum(a[2:],b[4:6])-np.maximum(a[:2],b[2:4]));inter=np.prod(bb);union=np.prod(a[2:]-a[:2])+np.prod(b[4:6]-b[2:4])-inter
            iou=inter/max(union,1e-9)
            if iou>best:best=float(iou);score_diff=float(abs(region['Score']-b[1]))
        matches.append(dict(label=region['Label'],iou=best,scoreError=score_diff))
    passed=len(det)==len(matches) and all(m['iou']>.98 and m['scoreError']<.002 for m in matches)
    results.append(dict(variant=v,officialRevision='7dbfcce3154a55776dc71ca026a4a2a8388dad8d' if v=='m' else 'ca760c3336d922ee63cdbff85003181222e3911c',preprocessMaxError=max_error,preprocessMeanError=mean_error,officialRegions=len(det),csharpRegions=len(matches),officialMs=elapsed,matches=matches,passed=passed))
    del predictor
(report/'reference.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
assert all(r['passed'] for r in results),results
print('M/L official Paddle parity passed')
