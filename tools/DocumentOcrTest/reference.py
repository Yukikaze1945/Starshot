"""Isolated upstream parity oracle. Not shipped with the application."""
import sys, pathlib, json, shutil, urllib.request, re, ast, types
import numpy as np
import onnxruntime as ort
from PIL import Image

root, models, report = map(pathlib.Path, sys.argv[1:4])
repo = pathlib.Path(__file__).resolve().parents[2]
reference = repo / 'build/document-ocr/reference'
reference.mkdir(parents=True,exist_ok=True)
fixtures=json.loads((root/'fixtures.json').read_text())
native=json.loads((report.parent/'inference.json').read_text(encoding='utf-8'))
rows={r['Name']:r for r in native['rows'] if 'Name' in r}
result={'inputNote':'Both runners receive correctly composited BT.601 grayscale; upstream RGB-to-BGR loader quirk is intentionally bypassed.','formula':[],'table':[],'layout':None}
try:
    package=reference/'rapid_latex_ocr';package.mkdir(exist_ok=True)
    for name in ['main.py','models.py','utils.py','utils_load.py','config.yaml']:
        shutil.copy(reference/name,package/name)
    (package/'__init__.py').write_text('from .main import LaTeXOCR\n')
    sys.path.insert(0,str(reference))
    from rapid_latex_ocr import LaTeXOCR
    from rapid_latex_ocr.utils_load import OrtInferSession
    from rapid_latex_ocr.models import Decoder
    original_init=OrtInferSession.__init__
    OrtInferSession.__init__=lambda self,path,num_threads=-1:original_init(self,path,2)
    Decoder.multinomial=staticmethod(lambda weights,num_samples,replacement=True:np.array([int(np.argmax(weights))]))
    engine=LaTeXOCR(image_resizer_path=models/'image_resizer.onnx',encoder_path=models/'encoder.onnx',decoder_path=models/'decoder.onnx',tokenizer_json=models/'tokenizer.json')
    for f in fixtures:
        if f['kind']!='formula':continue
        gray_input=np.asarray(Image.open(root/(f['name']+'.png')).convert('L'))
        text,elapsed=engine(gray_input)
        traces=[]
        old_process=engine.pre_process
        def trace_process(*args):
            final,pad=old_process(*args);traces.append(np.asarray(pad).copy());return final,pad
        engine.pre_process=trace_process
        tensor=engine.loop_image_resizer(engine.load_img(gray_input))
        engine.pre_process=old_process
        initial=np.asarray(engine.pre_pro.pad(Image.fromarray(engine.load_img(gray_input))))
        csi=np.fromfile(root/(f['name']+'.initial.bin'),dtype=np.uint8).reshape(json.loads((root/(f['name']+'.initial.json')).read_text()))
        trace_meta=json.loads((root/(f['name']+'.trace.json')).read_text());trace_errors=[]
        for i,p in enumerate(traces):
            if i>=len(trace_meta):break
            t=trace_meta[i];cs_gray=np.fromfile(root/(f['name']+'.trace-'+str(i)+'.bin'),dtype=np.uint8).reshape(t['height'],t['width'])
            trace_errors.append(dict(referenceShape=list(p.shape),csharp=t,maxError=int(np.max(np.abs(p.astype(int)-cs_gray.astype(int)))) if p.shape==cs_gray.shape else None))
        cs=np.fromfile(root/(f['name']+'.formula.bin'),dtype=np.float32).reshape(json.loads((root/(f['name']+'.shape.json')).read_text()))
        tensor_same=tensor.shape==cs.shape
        tensor_max=float(np.max(np.abs(tensor-cs))) if tensor_same else None
        actual=rows[f['name']]['formula']['Latex'];same=re.sub(r'\s+','',actual)==re.sub(r'\s+','',text)
        result['formula'].append(dict(name=f['name'],upstream=text,csharp=actual,equal=same,referenceMs=elapsed*1000,referenceShape=list(tensor.shape),csharpShape=list(cs.shape),tensorMaxError=tensor_max,initialShapes=[list(initial.shape),list(csi.shape)],initialMaxError=int(np.max(np.abs(initial.astype(int)-csi.astype(int)))) if initial.shape==csi.shape else None,trace=trace_errors))
    del engine
    source=(reference/'table_processors.py').read_text(encoding='utf-8')
    tree=ast.parse(source);node=next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='TableLabelDecode')
    scope={'np':np,'benchmark':types.SimpleNamespace(timeit=lambda c:c)}
    exec(compile(ast.Module(body=[node],type_ignores=[]),'<upstream TableLabelDecode>','exec'),scope)
    import yaml
    config=yaml.safe_load((models/'table.yml').read_text())
    decode=scope['TableLabelDecode']('SLANet_plus',dict_character=config['PostProcess']['character_dict'])
    opts=ort.SessionOptions();opts.intra_op_num_threads=2;opts.inter_op_num_threads=1;opts.log_severity_level=3
    session=ort.InferenceSession(str(models/'table.onnx'),opts,providers=['CPUExecutionProvider'])
    for f in fixtures:
        if f['kind']!='table':continue
        tensor=np.fromfile(root/(f['name']+'.input.bin'),dtype=np.float32).reshape(1,3,488,488)
        import cv2
        bgr=cv2.imread(str(root/(f['name']+'.png')));scale=488/max(bgr.shape[:2]);small=cv2.resize(bgr,(round(bgr.shape[1]*scale),round(bgr.shape[0]*scale)))
        normalized=(small.astype(np.float32)/255-np.array([.485,.456,.406],np.float32))/np.array([.229,.224,.225],np.float32)
        independently_prepared=np.zeros((488,488,3),np.float32);independently_prepared[:small.shape[0],:small.shape[1]]=normalized;independently_prepared=independently_prepared.transpose(2,0,1)[None]
        preprocess_max=float(np.max(np.abs(tensor-independently_prepared)))
        raw=session.run(None,{'x':independently_prepared});decoded=decode(raw,[(488,488)],[(f['width'],f['height'])])[0]
        native_table=rows[f['name']]['table'];count=len(decoded['bbox'])
        import xml.etree.ElementTree as ET
        html=''.join(decoded['structure']);tree=ET.fromstring(html);occupied=set();reference_structure=[]
        for r,tr in enumerate(tree.findall('.//tr')):
            column=0
            for td in tr:
                if td.tag not in ['td','th']:continue
                while (r,column) in occupied:column+=1
                rs=int(td.attrib.get('rowspan','1'));cs=int(td.attrib.get('colspan','1'));reference_structure.append([r,column,rs,cs]);occupied.update((yy,xx) for yy in range(r,r+rs) for xx in range(column,column+cs));column+=cs
        structure_equal=reference_structure==[[c['Row'],c['Column'],c['RowSpan'],c['ColSpan']] for c in native_table['Cells']]
        result['table'].append(dict(name=f['name'],referenceCells=count,csharpCells=len(native_table['Cells']),sameCellCount=count==len(native_table['Cells']),preprocessMaxError=preprocess_max,structureEqual=structure_equal,structure=decoded['structure'],boxes=decoded['bbox']))
    # A real Paddle predictor over the exact input tensor used by C#.
    official=reference/'paddle-layout';official.mkdir(exist_ok=True)
    base='https://huggingface.co/PaddlePaddle/PP-DocLayout-S/resolve/8ac289e66575bb9bba6e15c53719d8b15cc9b3b2/'
    for name in ['inference.json','inference.pdiparams']:
        if not (official/name).exists():urllib.request.urlretrieve(base+name,official/name)
    import paddle.inference as pi
    config=pi.Config(str(official/'inference.json'),str(official/'inference.pdiparams'))
    config.disable_gpu();config.set_cpu_math_library_num_threads(2);config.disable_glog_info()
    predictor=pi.create_predictor(config)
    page=next(f for f in fixtures if f['kind']=='layout')
    tensor=np.fromfile(root/'layout-input.bin',dtype=np.float32).reshape(1,3,480,480)
    for name in predictor.get_input_names():
        value=tensor if name=='image' else np.array([[480/page['height'],480/page['width']]],dtype=np.float32)
        handle=predictor.get_input_handle(name);handle.reshape(value.shape);handle.copy_from_cpu(value)
    predictor.run();values=[predictor.get_output_handle(n).copy_to_cpu() for n in predictor.get_output_names()]
    dets=next(v for v in values if v.ndim==2 and v.shape[-1]==6)
    dets=dets[dets[:,1]>=.3];actual=rows['page']['regions'];matches=[]
    for region in actual:
        box=region['Box'];a=np.array([box['X'],box['Y'],box['X']+box['Width'],box['Y']+box['Height']]);candidates=dets[dets[:,0]==region['ClassId']]
        best=0;score_error=None
        for d in candidates:
            b=d[2:6];inter=np.maximum(0,np.minimum(a[2:],b[2:])-np.maximum(a[:2],b[:2])).prod();union=np.maximum(0,a[2:]-a[:2]).prod()+np.maximum(0,b[2:]-b[:2]).prod()-inter;iou=float(inter/max(1e-9,union))
            if iou>best:best=iou;score_error=abs(float(d[1])-region['Score'])
        matches.append(dict(id=region['Id'],iou=best,scoreError=score_error))
    passed=len(dets)==len(actual) and bool(matches) and all(m['iou']>.98 and m['scoreError']<.005 for m in matches)
    result['layout']=dict(passed=passed,officialRevision='8ac289e66575bb9bba6e15c53719d8b15cc9b3b2',paddleCount=len(dets),csharpCount=len(actual),matches=matches)
    result['ok']=passed and all(x['equal'] for x in result['formula']) and all(x['sameCellCount'] and x['structureEqual'] and x['preprocessMaxError']<.024 for x in result['table'])
except Exception as exc:
    import traceback
    result['ok']=False;result['error']=traceback.format_exc()
finally:
    report.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'ok':result['ok'],'report':str(report)}),flush=True)
if not result['ok']:sys.exit(1)
