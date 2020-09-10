import os
import pydicom
import scipy.ndimage
import tkinter as tk
import tkinter.filedialog


class Dicom_file():
	def __init__(self, window):
		'''Read a dicom file and access the attributes using
		 .DD.<attribute>
		'''
		self.dicom_file_directory = tk.StringVar()
		self.get_file_directory()
		#print(self.dicom_file_directory.get())
		self.DD = self.read_file_data(directory = self.dicom_file_directory.get())
		#print(dir(self.DD))
	
	def get_file_directory(self):
		
		f = tk.filedialog.askopenfilename(parent=root, title="Choose file")
		self.dicom_file_directory.set(f)
		
	def read_file_data(self, directory):
		
		literal_directory = r"{}".format(directory)
		print(literal_directory)
		
		DD = pydicom.dcmread(literal_directory)
		return DD	

root = tk.Tk()


f = Dicom_file(root)
print("file open")
print("\n\n\n\\")
print(f)
print(f.DD.Columns[0],DataType)
print('is little endian: ',f.DD.is_little_endian)
print('Vr Implict: ',type(f.DD.is_implicit_VR))
#print(f.DD.dir())
#print(f.DD.PatientID, ' ', f.DD.PatientName)
#print(f.DD.get('(0008,0012)'))
#print(f.DD.keys())
'''
print('\n\n\n')
print(f.DD.InstanceCreationDate)
print(f.DD.InstanceCreationTime)

print(f.DD.StudyDate)
print(f.DD.SeriesDate)
print(f.DD.ContentDate)
print('\n\n\n\n')
print(f.DD.StudyTime)
print(f.DD.SeriesTime)
print(f.DD.ContentTime)
print(f.DD.OperatorsName)

print("SOPCLASSUID TYPE: ",f.DD.SOPClassUID.type)
'''


for value in f.DD.keys():
	
	print(value, ' ',f.DD.get(value))
	var_type = f.DD.get(value)
	print("TYPE: ", type(var_type),'\n\n')

#print(f.DD.ROIContourSequence)

#print("\n\n\n\\")
#print(f.DD.PixelSpacing) #example access attribute from dictionary

#print(f.DD)  #print the whole file


#root.mainloop()
