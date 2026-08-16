ALTER TABLE hm_messages ADD messageemailid nvarchar(48) not null CONSTRAINT df_messageemailid DEFAULT ''

update hm_messages set messageemailid = 'M0' + convert(nvarchar(20), messageid)

update hm_dbversion set value = 6015
